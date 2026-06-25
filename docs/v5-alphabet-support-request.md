# Feature Request: Native v5 Alphabet XML Format Support

## Summary

DasherCore's `AlphIO` parser only supports the v6 alphabet XML format. Users upgrading from Dasher v5 have custom alphabet files in v5 format that the parser silently rejects. All three frontends (Apple, Windows, GTK) need v5 support for migration (RFC 0005). Currently Dasher-Windows implements a workaround converter in the frontend; this should live in DasherCore so all platforms benefit.

## Background

Dasher v5 shipped for 10+ years. Users (particularly AAC users like Steve Saling) have invested significant effort creating custom alphabets with emojis, special symbols, and tailored character sets. These files use a different XML schema that v6's parser rejects silently — the alphabet simply doesn't appear in the alphabet list.

RFC 0005 (v5→v6 migration) requires importing these files on all platforms. Currently only Dasher-Windows handles this, via a C# XML converter that transforms v5→v6 before copying. This is duplicated effort that should be centralised.

## Format Differences

### Root Element

**v5:** `<alphabets>` wrapper containing one or more `<alphabet>` children
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE alphabets SYSTEM "alphabet.dtd">
<alphabets langcode="en-GB">
  <alphabet name="English with everything incl Emojis">
    ...
  </alphabet>
</alphabets>
```

**v6:** `<alphabet>` is the root element directly
```xml
<?xml version="1.0" encoding="UTF-8"?>
<alphabet name="English with limited punctuation" orientation="LR"
          trainingFilename="training_english_GB.txt" colorsName="Default">
  ...
</alphabet>
```

### Metadata

**v5:** Child elements
```xml
<orientation type="LR"/>
<encoding type="Western"/>
<palette>EurasianDarkMode</palette>
<train>training_english_GB.txt</train>
```

**v6:** Attributes on `<alphabet>`
```xml
<alphabet ... orientation="LR" trainingFilename="training_english_GB.txt" colorsName="Default">
```

Mapping:
| v5 element | v6 attribute |
|---|---|
| `<train>file.txt</train>` | `trainingFilename="file.txt"` |
| `<palette>Name</palette>` | `colorsName="Name"` |
| `<orientation type="LR"/>` | `orientation="LR"` |

### Symbols

**v5:** `<s>` elements with `d` (display), `t` (text), `b` (color index) attributes, no children
```xml
<s d="a" t="a" b="10"/>
<s d="😀" t="😀" b="125"/>
```

**v6:** `<node>` elements with `label`/`text` attributes and `<textCharAction>` children
```xml
<node label="a"><textCharAction /></node>
<node label="□"><textCharAction unicode="32" /></node>
```

### Groups

**v5:** `b` attribute (numeric color index), optional `visible` attribute
```xml
<group name="Lower case Latin letters" b="0" visible="off">
```

**v6:** `colorInfoName` attribute (named color group)
```xml
<group name="Lower case Latin letters" colorInfoName="lowercase">
```

### Special Characters

**v5:** Dedicated elements as direct children of `<alphabet>`
```xml
<space d="◇" t=" " b="9"/>
<control d="Control" t="" b="8"/>
<paragraph d="¶" b="9"/>
```

**v6:** Regular `<node>` elements inside a `paragraphSpace` group
```xml
<group name="paragraphSpace" colorInfoName="paragraphSpace">
  <node label="□"><textCharAction unicode="32" /></node>
  <node label="¶"><textCharAction /></node>
</group>
```

## Required Changes in `AlphIO.cpp`

### 1. Handle `<alphabets>` root

In `Parse()`, check if the root is `<alphabets>`. If so, iterate `<alphabet>` children and parse each. If root is `<alphabet>`, parse directly (existing behaviour).

**IMPORTANT — do NOT split into ParseSingle().** A previous attempt (PR #28) split `Parse()` into `Parse()` + `ParseSingle()` dispatch. This broke the space character in standard v6 alphabets for reasons we could not isolate despite extensive bisecting. Individual changes worked; only the function split broke things. Keep all parsing logic inside the existing `Parse()` method.

Suggested approach — handle the `<alphabets>` case at the top of `Parse()`, then fall through to the existing inline code:

```cpp
bool Dasher::CAlphIO::Parse(pugi::xml_document& document, const std::string, bool bUser) {
    pugi::xml_node root = document.document_element();

    // v5 format: <alphabets> wrapper — extract first <alphabet> child
    if (std::strcmp(root.name(), "alphabets") == 0) {
        root = root.child("alphabet");
        if (!root) return false;
    }

    // Existing code continues here with 'root' as the <alphabet> node
    if (std::strcmp(root.name(), "alphabet") != 0) return false;
    // ...
}
```

### 2. Recognise `<s>` elements in ParseGroupRecursive

Add `"s"` alongside `"node"` in the symbol check:

```cpp
if (std::strcmp(node.name(), "node") == 0 || std::strcmp(node.name(), "s") == 0) {
    // existing symbol creation code
}
```

### 3. Map v5 attributes in ReadCharAttributes

After reading v6 attributes, fall back to v5 names if empty:

```cpp
alphabet_character.Display = xml_node.attribute("label").as_string();
if (alphabet_character.Display.empty())
    alphabet_character.Display = xml_node.attribute("d").as_string();

alphabet_character.Text = xml_node.attribute("text").as_string(alphabet_character.Display.c_str());
if (alphabet_character.Text == alphabet_character.Display) {
    // v5 fallback: try 't' attribute
    auto tAttr = xml_node.attribute("t");
    if (!tAttr.empty())
        alphabet_character.Text = tAttr.as_string();
}
```

**Note:** Do NOT change the initial `Text` assignment from `as_string(Display.c_str())` to `as_string()`. The original behaviour (defaulting Text to Display) is relied upon by v6 nodes like the space character (`<node label="□">` with no `text` attribute). Changing this breaks the `textCharAction unicode="32"` path.

### 4. Default text actions for symbols without children

v5 `<s>` elements have no action children. After the action-parsing loop, if `DoActions` is still empty and `Text` is non-empty, create default actions:

```cpp
// At end of ReadCharAttributes, after the action loop:
if (DoActions.empty() && !alphabet_character.Text.empty()) {
    DoActions.push_back(new TextOutputAction(alphabet_character.Text));
    UndoActions.push_back(new TextDeleteAction(alphabet_character.Text));
}
```

### 5. Default colorGroup for v5 groups

v5 groups use `b` (numeric color index) instead of `colorInfoName`. Empty `colorGroup` causes `GetNodeColor` to return `undefinedColor` (Alpha=-1), which renders as fully transparent. Default to `"lowercase"`:

```cpp
pNewGroup->colorGroup = group_node.attribute("colorInfoName").as_string("");
if (pNewGroup->colorGroup.empty())
    pNewGroup->colorGroup = "lowercase";
```

### 6. Handle v5 metadata child elements (optional but recommended)

After reading v6 attributes, check for v5 child elements as fallbacks:

```cpp
// Only override if v6 attribute was empty
if (CurrentAlphabet->TrainingFile.empty()) {
    auto trainEl = alphabet.child("train");
    if (trainEl) CurrentAlphabet->TrainingFile = trainEl.text().as_string();
}
if (CurrentAlphabet->PreferredColors.empty()) {
    auto paletteEl = alphabet.child("palette");
    if (paletteEl) CurrentAlphabet->PreferredColors = paletteEl.text().as_string();
}
```

## Test Cases

1. **Standard v6 alphabet loads correctly** (regression — space character must appear)
2. **v5 alphabet with `<alphabets>` root loads correctly**
3. **v5 `<s>` symbols produce correct text output when selected**
4. **v5 custom control XML colours render visibly** (not transparent)
5. **Steve Saling's alphabet** (`alphabet.englishSS.xml` from DasherSmoothSaling repo):
   - 6 groups, 130 symbols including 11 emojis
   - Space and paragraph characters present
   - Training file loads from `<train>` element
6. **No crashes when mixing v5 and v6 alphabet files** in the same data directory

## What Went Wrong Last Time (PR #28)

PR #28 implemented all the above changes but also split `Parse()` into `Parse()` + `ParseSingle()`. The function split broke the space character (□) in standard v6 alphabets — the node disappeared from the canvas entirely.

Extensive bisecting showed:
- Each individual change worked fine
- All changes combined WITHOUT the function split worked fine
- The function split alone worked fine
- The function split + other changes combined broke things

We could not isolate the root cause despite removing changes one at a time. The recommendation is to keep all changes inside the existing `Parse()` method and avoid refactoring its structure.

## Reference Files

- v5 example: `DasherSmoothSaling/alphabet.englishSS.xml` (Steve Saling's custom alphabet)
- v6 example: `DasherCore/Data/alphabets/alphabet.english.with.limited.punctuation.xml`
- Current workaround: `Dasher-Windows/src/Dasher.Windows/Services/V5MigrationService.cs` (`ConvertV5AlphabetToV6` method)
- RFC 0005: `governance/rfcs/0005-v5-migration.md`
