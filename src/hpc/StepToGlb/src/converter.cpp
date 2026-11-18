#include "converter.hpp"
#include <BRepCheck_Analyzer.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRep_Tool.hxx>
#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Poly_Triangulation.hxx>
#include <RWGltf_CafWriter.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <TDocStd_Document.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <XCAFApp_Application.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <sstream>

namespace {
std::string escape(const std::string& value) {
  std::ostringstream out;
  for (const unsigned char ch : value) {
    switch (ch) {
    case '"': out << "\\\""; break;
    case '\\': out << "\\\\"; break;
    case '\n': out << "\\n"; break;
    case '\r': out << "\\r"; break;
    case '\t': out << "\\t"; break;
    default:
      if (ch < 32) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << int(ch);
      else out << ch;
    }
  }
  return out.str();
}
std::uint32_t u32(std::istream& in) {
  unsigned char b[4]{};
  in.read(reinterpret_cast<char*>(b), 4);
  return std::uint32_t(b[0]) | (std::uint32_t(b[1]) << 8) | (std::uint32_t(b[2]) << 16) | (std::uint32_t(b[3]) << 24);
}
struct Document {
  Handle(TDocStd_Document) value;
  Document() { XCAFApp_Application::GetApplication()->NewDocument("BinXCAF", value); }
  ~Document() { if (!value.IsNull()) XCAFApp_Application::GetApplication()->Close(value); }
};
}
namespace cad {
std::string progress_json(const Progress& p) {
  std::ostringstream s;
  s << "{\"stage\":\"" << escape(p.stage) << "\",\"percent\":";
  if (p.percent) s << *p.percent; else s << "null";
  s << ",\"message\":\"" << escape(p.message) << "\"}";
  return s.str();
}
bool valid_glb(const std::filesystem::path& path) {
  std::ifstream in(path, std::ios::binary);
  if (!in || u32(in) != 0x46546c67 || u32(in) != 2) return false;
  const auto length = u32(in);
  if (length < 28 || length != std::filesystem::file_size(path)) return false;
  std::uint64_t offset = 12;
  bool json = false, binary = false;
  while (offset < length) {
    const auto size = u32(in), kind = u32(in);
    if (!in || size == 0 || size % 4 || offset + 8 + size > length) return false;
    if (offset == 12) { if (kind != 0x4e4f534a) return false; json = true; }
    else if (kind == 0x004e4942) binary = true;
    in.seekg(size, std::ios::cur);
    offset += 8 + size;
  }
  return json && binary && offset == length;
}
void convert(const Options& o, const Reporter& report) {
  if (!std::isfinite(o.linear_deflection) || o.linear_deflection <= 0 ||
      !std::isfinite(o.angular_deflection) || o.angular_deflection <= 0 || o.angular_deflection > 3.141592653589793)
    throw ConversionError(2, "Deflection must be finite and positive; angular deflection must be <= pi radians");
  if (!std::filesystem::is_regular_file(o.input)) throw ConversionError(3, "Input STEP file does not exist");
  if (std::filesystem::absolute(o.input).lexically_normal() == std::filesystem::absolute(o.output).lexically_normal() ||
      (std::filesystem::exists(o.output) && std::filesystem::equivalent(o.input, o.output)))
    throw ConversionError(2, "Input and output must be different files");
  // OCCT's default messenger writes human-readable diagnostics to stdout.
  // Keep stdout exclusively JSON; diagnostics are represented by caught failures.
  Message::DefaultMessenger()->ChangePrinters().Clear();
  Document doc;
  XCAFDoc_DocumentTool::SetLengthUnit(doc.value, 0.001);
  STEPCAFControl_Reader reader;
  reader.SetNameMode(true);
  reader.SetColorMode(true);
  reader.SetLayerMode(true);
  report({"reading", {}, "Reading STEP entities"});
  if (reader.ReadFile(o.input.string().c_str()) != IFSelect_RetDone) throw ConversionError(3, "STEP read failed");
  reader.ChangeReader().SetSystemLengthUnit(1.0); // millimetres
  report({"importing", {}, "Transferring STEP assembly to XCAF"});
  if (!reader.Transfer(doc.value)) throw ConversionError(4, "STEP transfer failed or model is empty");
  auto tool = XCAFDoc_DocumentTool::ShapeTool(doc.value->Main());
  TDF_LabelSequence roots;
  tool->GetFreeShapes(roots);
  if (roots.IsEmpty()) throw ConversionError(4, "STEP contains no free shapes");
  report({"meshing", {}, "Validating and tessellating CAD faces"});
  std::size_t triangles = 0;
  for (int i = 1; i <= roots.Length(); ++i) {
    const auto shape = tool->GetShape(roots.Value(i));
    if (shape.IsNull() || !BRepCheck_Analyzer(shape).IsValid()) throw ConversionError(4, "Invalid imported geometry");
    BRepMesh_IncrementalMesh mesher(shape, o.linear_deflection, false, o.angular_deflection, true);
    if (!mesher.IsDone()) throw ConversionError(5, "Tessellation failed");
    for (TopExp_Explorer faces(shape, TopAbs_FACE); faces.More(); faces.Next()) {
      TopLoc_Location location;
      auto mesh = BRep_Tool::Triangulation(TopoDS::Face(faces.Current()), location);
      if (mesh.IsNull() || mesh->NbTriangles() == 0) throw ConversionError(5, "A CAD face has no triangulation");
      triangles += mesh->NbTriangles();
    }
  }
  if (triangles == 0) throw ConversionError(4, "Model has no surface geometry to export");
  if (!o.output.parent_path().empty()) std::filesystem::create_directories(o.output.parent_path());
  // Write beside final output, validate, then rename atomically. No partial GLB is published.
  const auto temporary = std::filesystem::path(o.output.string() + ".partial.glb");
  struct Cleanup { std::filesystem::path path; ~Cleanup() { std::error_code ec; std::filesystem::remove(path, ec); } } cleanup{temporary};
  report({"exporting", {}, "Writing binary glTF with names, colours and assembly transforms"});
  RWGltf_CafWriter writer(temporary.string().c_str(), true);
  writer.ChangeCoordinateSystemConverter().SetInputLengthUnit(0.001);
  writer.ChangeCoordinateSystemConverter().SetInputCoordinateSystem(RWMesh_CoordinateSystem_Zup);
  writer.ChangeCoordinateSystemConverter().SetOutputCoordinateSystem(RWMesh_CoordinateSystem_Yup);
  TColStd_IndexedDataMapOfStringString metadata;
  metadata.Add("Generator", "STEP-to-GLB / OCCT 7.9.3");
  if (!writer.Perform(doc.value, metadata, Message_ProgressRange())) throw ConversionError(6, "GLB export failed");
  report({"validating", {}, "Checking GLB container and nonempty geometry"});
  if (!valid_glb(temporary)) throw ConversionError(6, "GLB container validation failed");
  std::filesystem::rename(temporary, o.output);
  report({"converted", {}, "Local GLB validated; output upload and final confirmation pending"});
}
}
