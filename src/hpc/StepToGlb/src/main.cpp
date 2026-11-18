#include "converter.hpp"
#include <Standard_Failure.hxx>
#include <iostream>
#include <cstdlib>

int main(int argc, char** argv) {
  try {
    cad::Options options;
    bool input = false, output = false;
    for (int i = 1; i < argc; ++i) {
      const std::string key = argv[i];
      if (key == "--help") {
        std::cout << "step-to-glb --input file.step --output model.glb [--linear-deflection 0.1] [--angular-deflection 0.5]\n";
        return 0;
      }
      if (i + 1 >= argc) throw cad::ConversionError(2, "Missing argument value");
      const std::string value = argv[++i];
      if (key == "--input") { options.input = value; input = true; }
      else if (key == "--output") { options.output = value; output = true; }
      else if (key == "--linear-deflection" || key == "--angular-deflection") {
        std::size_t used;
        const auto number = std::stod(value, &used);
        if (used != value.size()) throw cad::ConversionError(2, "Invalid numeric argument");
        if (key == "--linear-deflection") options.linear_deflection = number;
        else options.angular_deflection = number;
      } else throw cad::ConversionError(2, "Unknown option: " + key);
    }
    if (!input || !output) throw cad::ConversionError(2, "--input and --output are required");
    cad::convert(options, [](const cad::Progress& p) { std::cout << cad::progress_json(p) << std::endl; });
    return 0;
  } catch (const cad::ConversionError& ex) { std::cerr << ex.what() << '\n'; return ex.exit_code; }
  catch (const std::bad_alloc&) { std::cerr << "Out of memory\n"; return 7; }
  catch (const Standard_Failure& ex) { std::cerr << "OCCT failure: " << ex.GetMessageString() << '\n'; return 4; }
  catch (const std::invalid_argument& ex) { std::cerr << ex.what() << '\n'; return 2; }
  catch (const std::out_of_range& ex) { std::cerr << ex.what() << '\n'; return 2; }
  catch (const std::exception& ex) { std::cerr << ex.what() << '\n'; return 6; }
}
