#include "core/Config.h"

#include <algorithm>
#include <cctype>
#include <fstream>
#include <sstream>
#include <stdexcept>

#include "core/Log.h"

namespace quiz {

namespace {

std::string_view Trim(std::string_view s) {
  while (!s.empty() && std::isspace(static_cast<unsigned char>(s.front()))) s.remove_prefix(1);
  while (!s.empty() && std::isspace(static_cast<unsigned char>(s.back()))) s.remove_suffix(1);
  return s;
}

std::string ToLower(std::string s) {
  std::transform(s.begin(), s.end(), s.begin(),
                 [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
  return s;
}

}  // namespace

bool Config::LoadFile(const std::string& path) {
  std::ifstream in(path, std::ios::binary);
  if (!in) return false;
  std::stringstream buffer;
  buffer << in.rdbuf();
  LoadText(buffer.str());
  return true;
}

void Config::LoadText(std::string_view text) {
  size_t lineNo = 0;
  while (!text.empty()) {
    const size_t newline = text.find('\n');
    std::string_view line = text.substr(0, newline);
    text = (newline == std::string_view::npos) ? std::string_view{} : text.substr(newline + 1);
    ++lineNo;

    if (const size_t hash = line.find('#'); hash != std::string_view::npos) line = line.substr(0, hash);
    line = Trim(line);
    if (line.empty()) continue;

    const size_t eq = line.find('=');
    if (eq == std::string_view::npos) {
      LOG_WARN("config: line %zu has no '=' - ignored", lineNo);
      continue;
    }
    const std::string key(Trim(line.substr(0, eq)));
    const std::string value(Trim(line.substr(eq + 1)));
    if (key.empty()) {
      LOG_WARN("config: line %zu has an empty key - ignored", lineNo);
      continue;
    }
    values_[key] = value;
  }
}

bool Config::Has(const std::string& key) const { return values_.find(key) != values_.end(); }

std::string Config::GetString(const std::string& key, const std::string& def) const {
  const auto it = values_.find(key);
  return it == values_.end() ? def : it->second;
}

int Config::GetInt(const std::string& key, int def) const {
  const auto it = values_.find(key);
  if (it == values_.end()) return def;
  try {
    size_t consumed = 0;
    const long value = std::stol(it->second, &consumed);
    if (consumed != it->second.size()) throw std::invalid_argument("trailing characters");
    return static_cast<int>(value);
  } catch (const std::exception&) {
    LOG_WARN("config: %s='%s' is not an integer - using %d", key.c_str(), it->second.c_str(), def);
    return def;
  }
}

double Config::GetDouble(const std::string& key, double def) const {
  const auto it = values_.find(key);
  if (it == values_.end()) return def;
  try {
    size_t consumed = 0;
    const double value = std::stod(it->second, &consumed);
    if (consumed != it->second.size()) throw std::invalid_argument("trailing characters");
    return value;
  } catch (const std::exception&) {
    LOG_WARN("config: %s='%s' is not a number - using %g", key.c_str(), it->second.c_str(), def);
    return def;
  }
}

bool Config::GetBool(const std::string& key, bool def) const {
  const auto it = values_.find(key);
  if (it == values_.end()) return def;
  const std::string v = ToLower(it->second);
  if (v == "true" || v == "yes" || v == "on" || v == "1") return true;
  if (v == "false" || v == "no" || v == "off" || v == "0") return false;
  LOG_WARN("config: %s='%s' is not a boolean - using %s", key.c_str(), it->second.c_str(), def ? "true" : "false");
  return def;
}

}  // namespace quiz
