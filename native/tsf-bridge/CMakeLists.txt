cmake_minimum_required(VERSION 3.21)
project(TsfBridge LANGUAGES CXX)

add_library(tsf_bridge STATIC TextContext.cpp)
target_compile_features(tsf_bridge PUBLIC cxx_std_20)
target_include_directories(tsf_bridge PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_definitions(tsf_bridge PUBLIC NOMINMAX UNICODE _UNICODE)
target_link_libraries(tsf_bridge PUBLIC ole32 uuid)

include(CTest)
if(BUILD_TESTING)
    add_executable(tsf_bridge_tests TextContextTests.cpp)
    target_link_libraries(tsf_bridge_tests PRIVATE tsf_bridge)
    set_property(TARGET tsf_bridge_tests PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    add_test(NAME tsf_bridge_tests COMMAND tsf_bridge_tests)
endif()



add_library(tsf_probe SHARED Probe.cpp Probe.def)
target_link_libraries(tsf_probe PRIVATE tsf_bridge user32 advapi32 shell32)
set_property(TARGET tsf_bridge tsf_probe PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")


if(BUILD_TESTING)
    add_executable(tsf_probe_tests ProbeTests.cpp)
    target_link_libraries(tsf_probe_tests PRIVATE tsf_bridge user32)
    set_property(TARGET tsf_probe_tests PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    add_test(NAME tsf_probe_tests COMMAND tsf_probe_tests $<TARGET_FILE:tsf_probe>)
endif()
