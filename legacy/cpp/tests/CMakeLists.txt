# 자체 완결 테스트 실행 파일 (외부 프레임워크 없음). CTest가 종료 코드로 판정한다.
# 이름 규칙: 파일 [system]_[feature]_test.cpp, 함수 test_[scenario]_[expected] (test-standards).
# unit/<system>/*_test.cpp 를 자동 수집해 파일 이름과 같은 타깃을 만든다.
# (Visual Studio 쪽은 테스트 하나당 .vcxproj 하나 — 새 테스트는 기존 것을 복사해 이름만 바꾼다.)

function(quiz_add_unit_test name source)
  add_executable(${name} ${source})
  target_link_libraries(${name} PRIVATE quiz_net)
  target_include_directories(${name} PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
  add_test(NAME ${name} COMMAND ${name})
endfunction()

file(GLOB QUIZ_TEST_SOURCES CONFIGURE_DEPENDS unit/*/*_test.cpp)
foreach(test_source ${QUIZ_TEST_SOURCES})
  get_filename_component(test_name ${test_source} NAME_WE)
  quiz_add_unit_test(${test_name} ${test_source})
endforeach()
