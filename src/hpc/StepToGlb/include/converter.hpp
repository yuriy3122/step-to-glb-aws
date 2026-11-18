#pragma once
#include <filesystem>
#include <functional>
#include <optional>
#include <stdexcept>
#include <string>

namespace cad {
struct Options {
  std::filesystem::path input;
  std::filesystem::path output;
  double linear_deflection = 0.1; // millimetres
  double angular_deflection = 0.5; // radians
};
struct Progress { std::string stage; std::optional<double> percent; std::string message; };
using Reporter = std::function<void(const Progress&)>;
class ConversionError : public std::runtime_error {
public:
  int exit_code;
  ConversionError(int code, const std::string& message) : std::runtime_error(message), exit_code(code) {}
};
void convert(const Options& options, const Reporter& report);
bool valid_glb(const std::filesystem::path& path);
std::string progress_json(const Progress& progress);
}
