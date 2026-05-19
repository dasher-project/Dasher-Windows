cmake_minimum_required(VERSION 3.20)
project(dasher_windows_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_compile_definitions(NOMINMAX)

set(BUILD_CAPI ON CACHE BOOL "" FORCE)

add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../DasherCore ${CMAKE_BINARY_DIR}/DasherCore)
