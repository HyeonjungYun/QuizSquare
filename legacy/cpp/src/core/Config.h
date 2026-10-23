#pragma once

#include <string>
#include <string_view>
#include <unordered_map>

namespace quiz {

/// `key = value` 텍스트 설정. 게임플레이·네트워크 수치는 코드에 박지 않고 여기서 읽는다.
/// 로드 후에는 읽기만 하므로 어느 스레드에서든 Get*을 호출해도 안전하다.
///
/// 예:
///   Config cfg;
///   if (!cfg.LoadFile("config/server.cfg")) { /* 파일 없음 */ }
///   const int port = cfg.GetInt("net.port", 7777);
class Config {
 public:
  /// 파일을 읽어 LoadText에 넘긴다. 파일을 열 수 없으면 false.
  bool LoadFile(const std::string& path);

  /// 줄 단위 `key = value`. '#' 이후는 주석. 잘못된 줄은 경고 로그 후 건너뛴다.
  /// 같은 키가 여러 번 나오면 마지막 값이 남는다.
  void LoadText(std::string_view text);

  bool Has(const std::string& key) const;
  std::string GetString(const std::string& key, const std::string& def) const;
  /// 정수가 아니면 경고 로그 후 def.
  int GetInt(const std::string& key, int def) const;
  double GetDouble(const std::string& key, double def) const;
  /// true/false, yes/no, on/off, 1/0 (대소문자 무시).
  bool GetBool(const std::string& key, bool def) const;

  size_t Size() const { return values_.size(); }

 private:
  std::unordered_map<std::string, std::string> values_;
};

}  // namespace quiz
