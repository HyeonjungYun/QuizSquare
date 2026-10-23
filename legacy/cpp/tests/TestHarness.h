#pragma once

// 최소 테스트 하네스. 외부 의존 없이 CHECK/RUN만 제공한다.
// 실패한 CHECK가 하나라도 있으면 main이 1을 돌려주고 CTest가 FAIL로 기록한다.

#include <cstdio>

inline int g_testFailures = 0;

#define CHECK(condition)                                                        \
  do {                                                                          \
    if (!(condition)) {                                                         \
      std::printf("    FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition);      \
      ++g_testFailures;                                                         \
    }                                                                           \
  } while (0)

#define CHECK_EQ(actual, expected)                                                                     \
  do {                                                                                                 \
    if (!((actual) == (expected))) {                                                                   \
      std::printf("    FAIL %s:%d: %s == %s\n", __FILE__, __LINE__, #actual, #expected);               \
      ++g_testFailures;                                                                                \
    }                                                                                                  \
  } while (0)

#define RUN_TEST(test)                                                      \
  do {                                                                      \
    const int before = g_testFailures;                                      \
    test();                                                                 \
    std::printf("[ %s ] %s\n", before == g_testFailures ? " OK " : "FAIL", #test); \
  } while (0)

inline int TestResult() {
  std::printf("%d failure(s)\n", g_testFailures);
  return g_testFailures == 0 ? 0 : 1;
}
