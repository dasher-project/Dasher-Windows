cmake_minimum_required(VERSION 3.20)
project(dasher_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_compile_definitions(NOMINMAX HAVE_OWN_FILEUTILS)

set(DASHER_HAVE_OWN_FILEUTILS ON CACHE BOOL "" FORCE)

add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../../DasherCore ${CMAKE_BINARY_DIR}/DasherCore)

add_library(dasher_native SHARED
    WinCommandScreen.cpp
    WinDasherInterface.cpp
    WinFileUtils.cpp
    WinBridge.cpp
)

target_include_directories(dasher_native PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
)

target_link_libraries(dasher_native PRIVATE DasherCore)

set_target_properties(dasher_native PROPERTIES
    WINDOWS_EXPORT_ALL_SYMBOLS ON
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)
