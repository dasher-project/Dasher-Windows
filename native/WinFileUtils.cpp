#include "DasherCore/FileUtils.h"

#include <fstream>
#include <ios>
#include <regex>
#include <sstream>
#include <string>
#include <vector>
#include <windows.h>

namespace {

std::string g_dataDir;

std::string joinPath(const std::string &a, const std::string &b) {
    if (a.empty()) return b;
    if (a.back() == '/' || a.back() == '\\') return a + b;
    return a + "\\" + b;
}

std::string resolvePath(const std::string &path) {
    if (path.empty()) return path;
    if (path.size() > 1 && (path[0] == '/' || path[1] == ':')) return path;
    if (g_dataDir.empty()) return path;
    return joinPath(g_dataDir, path);
}

bool parseFileFromDir(AbstractParser *parser, const std::string &dir, const std::string &fileName) {
    std::string fullPath = joinPath(dir, fileName);
    std::ifstream f(fullPath, std::ios::binary);
    if (!f.is_open()) return false;
    f.close();
    return parser->ParseFile(fullPath, true);
}

void scanDirectory(AbstractParser *parser, const std::string &dir, const std::regex &pattern) {
    if (!parser || dir.empty()) return;

    std::string searchPath = dir + "\\*";
    WIN32_FIND_DATAA findData;
    HANDLE hFind = FindFirstFileA(searchPath.c_str(), &findData);
    if (hFind == INVALID_HANDLE_VALUE) return;

    do {
        if (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        std::string fileName(findData.cFileName);
        if (!std::regex_search(fileName, pattern)) continue;
        parseFileFromDir(parser, dir, fileName);
    } while (FindNextFileA(hFind, &findData));

    FindClose(hFind);
}

}

extern "C" void WinSetDataDir(const char *dir) {
    g_dataDir = dir ? dir : "";
}

int Dasher::FileUtils::GetFileSize(const std::string &strFileName) {
    std::ifstream f(resolvePath(strFileName), std::ios::binary | std::ios::ate);
    if (!f.is_open()) return 0;
    return static_cast<int>(f.tellg());
}

void Dasher::FileUtils::ScanFiles(AbstractParser *parser, const std::string &strPattern) {
    if (!parser) return;

    std::ifstream local(resolvePath(strPattern), std::ios::binary);
    if (local.is_open()) {
        local.close();
        parser->ParseFile(resolvePath(strPattern), true);
    }

    std::regex pattern(strPattern);
    scanDirectory(parser, g_dataDir, pattern);

    std::string alphabetsDir = joinPath(g_dataDir, "alphabets");
    std::string colorsDir = joinPath(g_dataDir, "colors");
    std::string coloursDir = joinPath(g_dataDir, "colours");
    std::string settingsDir = joinPath(g_dataDir, "settings");
    std::string trainingDir = joinPath(g_dataDir, "training");

    scanDirectory(parser, alphabetsDir, pattern);
    scanDirectory(parser, colorsDir, pattern);
    scanDirectory(parser, coloursDir, pattern);
    scanDirectory(parser, settingsDir, pattern);
    scanDirectory(parser, trainingDir, pattern);
}

bool Dasher::FileUtils::WriteUserDataFile(const std::string &filename, const std::string &strNewText, bool append) {
    std::ofstream f(resolvePath(filename), append ? std::ios::app : std::ios::trunc);
    if (!f.is_open()) return false;
    f << strNewText;
    return f.good();
}

std::string Dasher::FileUtils::GetFullFilenamePath(const std::string strFilename) {
    return resolvePath(strFilename);
}
