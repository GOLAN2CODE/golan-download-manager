const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { auditI18n } = require("../../tests/check_i18n.js");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

test("I18N-01: every Loc.T / XAML i18n key in src/GDM exists in both AR and EN and all 13 domain files match 1-to-1", () => {
  const res = auditI18n(REPO_ROOT);
  assert.equal(res.missingDomainFiles.length, 0, `Missing domain files: ${res.missingDomainFiles.join("; ")}`);
  assert.equal(res.fileKeyMismatches.length, 0, `Per-file AR/EN mismatches: ${res.fileKeyMismatches.join("; ")}`);
  assert.equal(res.arOnly.length, 0, `Keys only in AR: ${res.arOnly.join("; ")}`);
  assert.equal(res.enOnly.length, 0, `Keys only in EN: ${res.enOnly.join("; ")}`);
  assert.equal(res.emptyValues.length, 0, `Empty translations: ${res.emptyValues.join("; ")}`);
  assert.equal(res.duplicateKeysAr.length, 0, `Duplicate AR keys: ${res.duplicateKeysAr.join("; ")}`);
  assert.equal(res.duplicateKeysEn.length, 0, `Duplicate EN keys: ${res.duplicateKeysEn.join("; ")}`);
  assert.equal(res.placeholderMismatches.length, 0, `Placeholder mismatches: ${res.placeholderMismatches.join("; ")}`);
  assert.equal(res.missingInAr.length, 0, `Code keys missing in AR: ${res.missingInAr.join("; ")}`);
  assert.equal(res.missingInEn.length, 0, `Code keys missing in EN: ${res.missingInEn.join("; ")}`);
  assert.equal(res.extensionMismatches.length, 0, `Extension i18n mismatches: ${res.extensionMismatches.join("; ")}`);
  assert.equal(res.ok, true);
  assert.equal(res.arKeyCount, res.enKeyCount);
  assert.ok(res.arKeyCount >= 1462, `Expected >= 1462 keys, got ${res.arKeyCount}`);
});

test("I18N-02: auditI18n detects orphan code keys, AR/EN imbalances, and placeholder arity bugs", () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "gdm-i18n-test-"));
  try {
    const i18nDir = path.join(tmp, "src", "GDM", "i18n");
    const uiDir = path.join(tmp, "src", "GDM", "UI");
    fs.mkdirSync(i18nDir, { recursive: true });
    fs.mkdirSync(uiDir, { recursive: true });

    fs.writeFileSync(path.join(i18nDir, "windows.ar.json"), JSON.stringify({
      "win.test.ok": "موافق",
      "win.test.ar_only": "عربي فقط",
      "win.test.fmt": "العنصر {0} من {1}"
    }), "utf8");
    fs.writeFileSync(path.join(i18nDir, "windows.en.json"), JSON.stringify({
      "win.test.ok": "OK",
      "win.test.en_only": "English only",
      "win.test.fmt": "Item {0}"
    }), "utf8");
    fs.writeFileSync(path.join(uiDir, "Sample.cs"), [
      'var a = Loc.T("win.test.ok");',
      'var b = Loc.T("win.test.missing_key");',
      'var c = Loc.T("win.test.fmt", 1);'
    ].join("\n"), "utf8");
    fs.writeFileSync(path.join(uiDir, "Sample.xaml"), '<Window Title="{core:T win.test.xaml_missing}" />', "utf8");

    const res = auditI18n(tmp);
    assert.equal(res.ok, false);
    assert.ok(res.arOnly.includes("win.test.ar_only"));
    assert.ok(res.enOnly.includes("win.test.en_only"));
    assert.ok(res.missingInAr.some(s => s.startsWith("win.test.missing_key")));
    assert.ok(res.missingInEn.some(s => s.startsWith("win.test.xaml_missing")));
    assert.ok(res.placeholderMismatches.some(s => s.includes("win.test.fmt")));
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});
