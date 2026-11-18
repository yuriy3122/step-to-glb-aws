#include "converter.hpp"
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRep_Builder.hxx>
#include <Interface_Static.hxx>
#include <Quantity_Color.hxx>
#include <STEPCAFControl_Writer.hxx>
#include <StepData_StepModel.hxx>
#include <TDataStd_Name.hxx>
#include <TDocStd_Document.hxx>
#include <TopoDS_Compound.hxx>
#include <XCAFApp_Application.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>
#include <fstream>
#include <iostream>
#include <limits>
#include <vector>

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
void fixture(const std::filesystem::path& file, bool assembly, bool empty = false, bool inches = false) {
  Handle(TDocStd_Document) doc;
  auto app = XCAFApp_Application::GetApplication();
  app->NewDocument("BinXCAF", doc);
  XCAFDoc_DocumentTool::SetLengthUnit(doc, 0.001);
  auto shapes = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
  if (empty) {
    BRep_Builder builder;
    TopoDS_Compound compound;
    builder.MakeCompound(compound);
    shapes->AddShape(compound, false);
  } else {
    auto box = shapes->AddShape(BRepPrimAPI_MakeBox(10, 20, 30).Shape(), false);
    TDataStd_Name::Set(box, "RedBox");
    XCAFDoc_DocumentTool::ColorTool(doc->Main())->SetColor(box, Quantity_Color(1.0, 0.0, 0.0, Quantity_TOC_RGB), XCAFDoc_ColorGen);
    if (assembly) {
      BRep_Builder builder;
      TopoDS_Compound compound;
      builder.MakeCompound(compound);
      auto root = shapes->AddShape(compound, true);
      TDataStd_Name::Set(root, "TwoBoxes");
      auto first = shapes->AddComponent(root, box, TopLoc_Location());
      TDataStd_Name::Set(first, "OriginBox");
      gp_Trsf translate;
      translate.SetTranslation(gp_Vec(50, 0, 0));
      auto second = shapes->AddComponent(root, box, TopLoc_Location(translate));
      TDataStd_Name::Set(second, "TranslatedBox");
      shapes->UpdateAssemblies();
    }
  }
  Interface_Static::SetCVal("write.step.unit", inches ? "INCH" : "MM");
  STEPCAFControl_Writer writer;
  writer.SetColorMode(true);
  writer.SetNameMode(true);
  if (inches) writer.ChangeWriter().Model()->SetWriteLengthUnit(25.4);
  require(writer.Transfer(doc, STEPControl_AsIs), "Fixture STEP transfer");
  require(writer.Write(file.string().c_str()) == IFSelect_RetDone, "Fixture STEP write");
  Interface_Static::SetCVal("write.step.unit", "MM");
  app->Close(doc);
}
void expect_error(cad::Options options, int code) {
  try { cad::convert(options, [](const auto&){}); }
  catch (const cad::ConversionError& ex) { require(ex.exit_code == code, "Wrong error code"); return; }
  throw std::runtime_error("Expected conversion failure");
}
int main(int argc, char** argv) {
  try {
    const std::filesystem::path dir = argc > 1 ? argv[1] : "fixtures";
    std::filesystem::create_directories(dir);
    fixture(dir / "box.step", false);
    fixture(dir / "assembly.step", true);
    fixture(dir / "inch-box.step", false, false, true);
    fixture(dir / "empty.step", false, true);
    std::ofstream(dir / "invalid.step") << "This is not a STEP model";
    std::vector<std::string> stages;
    cad::convert({dir / "box.step", dir / "box.glb"}, [&](const auto& p) {
      require(!p.percent.has_value(), "Do not invent percentage precision");
      stages.push_back(p.stage);
    });
    require(cad::valid_glb(dir / "box.glb"), "GLB header and binary chunks");
    require(stages == std::vector<std::string>({"reading", "importing", "meshing", "exporting", "validating", "converted"}), "Stage order");
    cad::convert({dir / "assembly.step", dir / "assembly.glb"}, [](const auto&){});
    require(cad::valid_glb(dir / "assembly.glb"), "Assembly GLB");
    cad::convert({dir / "inch-box.step", dir / "inch-box.glb"}, [](const auto&){});
    expect_error({dir / "invalid.step", dir / "invalid.glb"}, 3);
    // Empty STEP exports may be rejected during reading or transfer by OCCT.
    try { cad::convert({dir / "empty.step", dir / "empty.glb"}, [](const auto&){}); throw std::runtime_error("Accepted empty geometry"); }
    catch (const cad::ConversionError& ex) { require(ex.exit_code == 3 || ex.exit_code == 4, "Empty model error"); }
    expect_error({dir / "missing.step", dir / "missing.glb"}, 3);
    expect_error({dir / "box.step", dir / "bad.glb", 0, 0.5}, 2);
    expect_error({dir / "box.step", dir / "bad.glb", 0.1, -1}, 2);
    expect_error({dir / "box.step", dir / "bad.glb", std::numeric_limits<double>::quiet_NaN(), 0.5}, 2);
    expect_error({dir / "box.step", dir / "box.step"}, 2);
    std::ofstream(dir / "truncated.glb") << "glTF";
    require(!cad::valid_glb(dir / "truncated.glb"), "Reject truncated GLB");
    require(cad::progress_json({"a", {}, "quote\"\n"}) == "{\"stage\":\"a\",\"percent\":null,\"message\":\"quote\\\"\\n\"}", "JSON escaping");
    std::cout << "PASS: real STEP, assembly, empty/invalid/missing input, parameters, GLB chunks, progress\n";
    return 0;
  } catch (const std::exception& ex) { std::cerr << ex.what() << '\n'; return 1; }
}
