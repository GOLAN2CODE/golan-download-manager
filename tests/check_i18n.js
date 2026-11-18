#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');

function walkFiles(dir, exts, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'bin' || entry.name === 'obj' || entry.name === 'node_modules') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walkFiles(full, exts, out);
    } else if (exts.some(ext => entry.name.toLowerCase().endsWith(ext))) {
      out.push(full);
    }
  }
  return out;
}

function extractPlaceholders(str) {
  const set = new Set();
  const rx = /\{(\d+)(?:[:,][^}]*)?\}/g;
  let m;
  while ((m = rx.exec(str)) !== null) {
    set.add(m[1]);
  }
  return [...set].sort((a, b) => Number(a) - Number(b));
}

function hasInvalidFormatBraces(str) {
  const stripped = str.replace(/\{\{/g, '').replace(/\}\}/g, '');
  const left = stripped.replace(/\{\d+(?:[:,][^}]*)?\}/g, '');
  return /[{}]/.test(left);
}

function countCallFormatArgs(content, afterKeyOffset) {
  let i = afterKeyOffset;
  let depth = 1;
  let commas = 0;
  let inStr = false;
  let quote = '';
  let verbatim = false;
  while (i < content.length && depth > 0) {
    const c = content[i];
    if (inStr) {
      if (verbatim) {
        if (c === '"' && content[i + 1] === '"') { i += 2; continue; }
        if (c === quote) { inStr = false; verbatim = false; }
      } else {
        if (c === '\\') { i += 2; continue; }
        if (c === quote) inStr = false;
      }
    } else {
      if (c === '/' && content[i + 1] === '/') {
        while (i < content.length && content[i] !== '\n') i++;
        continue;
      }
      if (c === '@' && (content[i + 1] === '"' || (content[i + 1] === '$' && content[i + 2] === '"'))) {
        verbatim = true;
        inStr = true;
        quote = '"';
        i += content[i + 1] === '$' ? 3 : 2;
        continue;
      }
      if (c === '"' || c === "'") { inStr = true; quote = c; verbatim = false; }
      else if (c === '(' || c === '[' || c === '{') depth++;
      else if (c === ')' || c === ']' || c === '}') depth--;
      else if (c === ',' && depth === 1) commas++;
    }
    i++;
  }
  const argSlice = content.slice(afterKeyOffset, Math.max(afterKeyOffset, i - 1));
  if (/\bargs\b/.test(argSlice)) return null;
  return commas;
}

function extractCallArityChecks(srcGdmDir) {
  const calls = []; // { key, argCount, loc }
  const files = walkFiles(srcGdmDir, ['.cs', '.xaml']);
  for (const file of files) {
    const rel = path.relative(srcGdmDir, file).replace(/\\/g, '/');
    const content = fs.readFileSync(file, 'utf8');
    const lineOfOffset = (offset) => content.slice(0, offset).split('\n').length;
    if (file.toLowerCase().endsWith('.xaml')) {
      const xamlRx = /\{\s*[A-Za-z0-9_]+:T(?:Extension)?\s+(?:Key\s*=\s*)?['"]?([A-Za-z0-9_.-]+)['"]?\s*\}/g;
      let m;
      while ((m = xamlRx.exec(content)) !== null) {
        calls.push({ key: m[1], argCount: 0, loc: `${rel}:${lineOfOffset(m.index)}` });
      }
    } else {
      const csCallRx = /\b(?:Loc\.T|V5T|TorrentText\.T)\s*\(\s*\$?"([A-Za-z0-9_.-]+)"/g;
      let m;
      while ((m = csCallRx.exec(content)) !== null) {
        const argCount = countCallFormatArgs(content, m.index + m[0].length);
        if (argCount !== null) {
          calls.push({ key: m[1], argCount, loc: `${rel}:${lineOfOffset(m.index)}` });
        }
      }
    }
  }
  return calls;
}

function extractUsedI18nKeys(srcGdmDir) {
  const used = new Map(); // key -> Set<relativeFile:line>
  const record = (key, relPath, lineNo) => {
    if (!key || key.includes('{') || key.includes('}')) return;
    if (!used.has(key)) used.set(key, new Set());
    used.get(key).add(`${relPath}:${lineNo}`);
  };

  const files = walkFiles(srcGdmDir, ['.cs', '.xaml']);
  for (const file of files) {
    const rel = path.relative(srcGdmDir, file).replace(/\\/g, '/');
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);

    if (file.toLowerCase().endsWith('.xaml')) {
      lines.forEach((line, idx) => {
        // Match {core:T key}, {core:TExtension key}, {v3:T key}, {v4:T key}, {vb:T key}, Key="key" inside TExtension
        const xamlRx = /\{\s*[A-Za-z0-9_]+:T(?:Extension)?\s+(?:Key\s*=\s*)?['"]?([A-Za-z0-9_.-]+)['"]?\s*\}/g;
        let m;
        while ((m = xamlRx.exec(line)) !== null) {
          record(m[1], rel, idx + 1);
        }
      });
      continue;
    }

    // C# file: scan line-by-line and full-text for multiline Loc.T(...) / V5T(...) / TorrentText.T(...)
    const content = lines.join('\n');
    const lineOfOffset = (offset) => content.slice(0, offset).split('\n').length;

    // 1. Direct string calls: Loc.T("key"), Loc.T($"key"), V5T("key"), TorrentText.T("key")
    const directRx = /\b(?:Loc\.T|V5T|TorrentText\.T)\s*\(\s*\$?"([A-Za-z0-9_.-]+)"/g;
    let m;
    while ((m = directRx.exec(content)) !== null) {
      record(m[1], rel, lineOfOffset(m.index));
    }

    // 2. Ternary calls: Loc.T(cond ? "keyA" : "keyB", ...)
    const ternaryRx = /\b(?:Loc\.T|V5T|TorrentText\.T)\s*\([^;()]*?\?\s*\$?"([A-Za-z0-9_.-]+)"\s*:\s*\$?"([A-Za-z0-9_.-]+)"/g;
    while ((m = ternaryRx.exec(content)) !== null) {
      const ln = lineOfOffset(m.index);
      record(m[1], rel, ln);
      record(m[2], rel, ln);
    }

    // 3. Switch expressions inside Loc.T(... switch { ... => "key", ... })
    const switchBlockRx = /\b(?:Loc\.T|V5T|TorrentText\.T)\s*\([^{;]*?\bswitch\s*\{([\s\S]*?)\}\s*\)/g;
    while ((m = switchBlockRx.exec(content)) !== null) {
      const body = m[1];
      const baseOffset = m.index + m[0].indexOf(body);
      const armRx = /=>\s*\$?"([A-Za-z0-9_.-]+)"/g;
      let arm;
      while ((arm = armRx.exec(body)) !== null) {
        record(arm[1], rel, lineOfOffset(baseOffset + arm.index));
      }
    }

    // 4. BrowserInfo("browser.name.*", ...) or similar dotted key constants passed to NameKey
    const browserKeyRx = /\bnew\s+BrowserInfo\s*\(\s*"([a-z0-9_]+\.[a-z0-9_.-]+)"/gi;
    while ((m = browserKeyRx.exec(content)) !== null) {
      record(m[1], rel, lineOfOffset(m.index));
    }
  }

  return used;
}

function auditExtensionI18n(repoRoot) {
  const extDir = path.join(repoRoot, 'extension');
  const arFile = path.join(extDir, '_locales', 'ar', 'messages.json');
  const enFile = path.join(extDir, '_locales', 'en', 'messages.json');
  if (!fs.existsSync(arFile) || !fs.existsSync(enFile)) {
    return { ok: true, mismatches: [] };
  }
  const arObj = JSON.parse(fs.readFileSync(arFile, 'utf8'));
  const enObj = JSON.parse(fs.readFileSync(enFile, 'utf8'));
  const mismatches = [];
  for (const [k, v] of Object.entries(arObj)) {
    if (!v || typeof v.message !== 'string' || v.message.trim().length === 0) {
      mismatches.push(`extension ar:${k} empty message`);
    }
    if (!(k in enObj)) mismatches.push(`extension ar:${k} missing from en`);
  }
  for (const [k, v] of Object.entries(enObj)) {
    if (!v || typeof v.message !== 'string' || v.message.trim().length === 0) {
      mismatches.push(`extension en:${k} empty message`);
    }
    if (!(k in arObj)) mismatches.push(`extension en:${k} missing from ar`);
  }
  for (const file of walkFiles(extDir, ['.js', '.html', '.json'])) {
    if (file.includes('_locales') || file.includes(path.sep + 'tests' + path.sep)) continue;
    const rel = path.relative(repoRoot, file).replace(/\\/g, '/');
    const txt = fs.readFileSync(file, 'utf8');
    let m;
    const getMsgRx = /\bi18n\.getMessage\s*\(\s*['"]([A-Za-z0-9_]+)['"]/g;
    while ((m = getMsgRx.exec(txt)) !== null) {
      if (!(m[1] in arObj)) mismatches.push(`extension key ${m[1]} (${rel}) missing from ar`);
      if (!(m[1] in enObj)) mismatches.push(`extension key ${m[1]} (${rel}) missing from en`);
    }
    const msgMacroRx = /__MSG_([A-Za-z0-9_]+)__/g;
    while ((m = msgMacroRx.exec(txt)) !== null) {
      if (m[1].startsWith('@')) continue;
      if (!(m[1] in arObj)) mismatches.push(`extension macro ${m[1]} (${rel}) missing from ar`);
      if (!(m[1] in enObj)) mismatches.push(`extension macro ${m[1]} (${rel}) missing from en`);
    }
  }
  return { ok: mismatches.length === 0, mismatches };
}

function auditI18n(repoRoot = path.resolve(__dirname, '..')) {
  const srcGdmDir = path.join(repoRoot, 'src', 'GDM');
  const i18nDir = path.join(srcGdmDir, 'i18n');

  if (!fs.existsSync(i18nDir)) {
    throw new Error(`i18n directory not found: ${i18nDir}`);
  }

  const jsonFiles = fs.readdirSync(i18nDir).filter(f => f.endsWith('.json')).sort();
  const domains = new Set();
  for (const f of jsonFiles) {
    const match = /^(.+)\.(ar|en)\.json$/.exec(f);
    if (match) domains.add(match[1]);
  }

  const missingDomainFiles = [];
  const fileKeyMismatches = [];
  const emptyValues = [];
  const duplicateKeysAr = [];
  const duplicateKeysEn = [];
  const placeholderMismatches = [];

  const arAll = new Map(); // key -> { value, domain }
  const enAll = new Map(); // key -> { value, domain }

  for (const domain of [...domains].sort()) {
    const arFile = path.join(i18nDir, `${domain}.ar.json`);
    const enFile = path.join(i18nDir, `${domain}.en.json`);
    if (!fs.existsSync(arFile)) { missingDomainFiles.push(`${domain}.ar.json`); continue; }
    if (!fs.existsSync(enFile)) { missingDomainFiles.push(`${domain}.en.json`); continue; }

    const arObj = JSON.parse(fs.readFileSync(arFile, 'utf8'));
    const enObj = JSON.parse(fs.readFileSync(enFile, 'utf8'));

    for (const [k, v] of Object.entries(arObj)) {
      if (typeof v !== 'string' || v.trim().length === 0) {
        emptyValues.push(`${domain}.ar.json:${k}`);
      }
      if (arAll.has(k)) {
        duplicateKeysAr.push(`${k} (${arAll.get(k).domain}.ar.json & ${domain}.ar.json)`);
      }
      arAll.set(k, { value: v, domain });
      if (!(k in enObj)) {
        fileKeyMismatches.push(`${k} in ${domain}.ar.json missing from ${domain}.en.json`);
      }
    }

    for (const [k, v] of Object.entries(enObj)) {
      if (typeof v !== 'string' || v.trim().length === 0) {
        emptyValues.push(`${domain}.en.json:${k}`);
      }
      if (enAll.has(k)) {
        duplicateKeysEn.push(`${k} (${enAll.get(k).domain}.en.json & ${domain}.en.json)`);
      }
      enAll.set(k, { value: v, domain });
      if (!(k in arObj)) {
        fileKeyMismatches.push(`${k} in ${domain}.en.json missing from ${domain}.ar.json`);
      }
    }
  }

  const arOnly = [];
  const enOnly = [];
  for (const k of arAll.keys()) {
    if (!enAll.has(k)) arOnly.push(k);
  }
  for (const k of enAll.keys()) {
    if (!arAll.has(k)) enOnly.push(k);
  }

  for (const [k, arEntry] of arAll.entries()) {
    if (hasInvalidFormatBraces(arEntry.value)) {
      placeholderMismatches.push(`${k} (${arEntry.domain}.ar.json): invalid format braces`);
    }
    const enEntry = enAll.get(k);
    if (!enEntry) continue;
    if (hasInvalidFormatBraces(enEntry.value)) {
      placeholderMismatches.push(`${k} (${enEntry.domain}.en.json): invalid format braces`);
    }
    const arPh = extractPlaceholders(arEntry.value).join(',');
    const enPh = extractPlaceholders(enEntry.value).join(',');
    if (arPh !== enPh) {
      placeholderMismatches.push(`${k} (${arEntry.domain}): ar={${arPh}} vs en={${enPh}}`);
    }
  }

  for (const { key, argCount, loc } of extractCallArityChecks(srcGdmDir)) {
    const entry = enAll.get(key) || arAll.get(key);
    if (!entry) continue;
    const phs = extractPlaceholders(entry.value).map(Number);
    if (phs.length > 0) {
      const maxPh = Math.max(...phs);
      if (argCount < maxPh + 1) {
        placeholderMismatches.push(`${key} called with ${argCount} arg(s) at ${loc} but expects at least ${maxPh + 1} ({${phs.join(',')}})`);
      }
    }
  }

  const usedKeys = extractUsedI18nKeys(srcGdmDir);
  const missingInAr = [];
  const missingInEn = [];
  for (const [key, locations] of [...usedKeys.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
    const locList = [...locations].join(', ');
    if (!arAll.has(key)) missingInAr.push(`${key} (used at ${locList})`);
    if (!enAll.has(key)) missingInEn.push(`${key} (used at ${locList})`);
  }

  const unusedInCode = [];
  for (const k of [...arAll.keys()].sort()) {
    if (!usedKeys.has(k)) unusedInCode.push(k);
  }

  const extAudit = auditExtensionI18n(repoRoot);

  const ok =
    missingDomainFiles.length === 0 &&
    fileKeyMismatches.length === 0 &&
    arOnly.length === 0 &&
    enOnly.length === 0 &&
    emptyValues.length === 0 &&
    duplicateKeysAr.length === 0 &&
    duplicateKeysEn.length === 0 &&
    missingInAr.length === 0 &&
    missingInEn.length === 0 &&
    placeholderMismatches.length === 0 &&
    extAudit.ok;

  return {
    ok,
    domainCount: domains.size,
    arKeyCount: arAll.size,
    enKeyCount: enAll.size,
    usedKeyCount: usedKeys.size,
    unusedKeyCount: unusedInCode.length,
    missingDomainFiles,
    fileKeyMismatches,
    arOnly,
    enOnly,
    emptyValues,
    duplicateKeysAr,
    duplicateKeysEn,
    placeholderMismatches,
    missingInAr,
    missingInEn,
    extensionMismatches: extAudit.mismatches,
    unusedInCode,
  };
}

if (require.main === module) {
  const showUnused = process.argv.includes('--unused');
  const res = auditI18n();
  console.log(`i18n audit: domains=${res.domainCount}, arKeys=${res.arKeyCount}, enKeys=${res.enKeyCount}, usedInCode=${res.usedKeyCount}, unusedKeys=${res.unusedKeyCount}`);
  const sections = [
    ['Missing domain files', res.missingDomainFiles],
    ['Per-file AR/EN key mismatches', res.fileKeyMismatches],
    ['Keys in AR missing from EN', res.arOnly],
    ['Keys in EN missing from AR', res.enOnly],
    ['Empty translation values', res.emptyValues],
    ['Duplicate keys across AR files', res.duplicateKeysAr],
    ['Duplicate keys across EN files', res.duplicateKeysEn],
    ['Placeholder {n} mismatches (AR vs EN)', res.placeholderMismatches],
    ['Code keys missing from AR', res.missingInAr],
    ['Code keys missing from EN', res.missingInEn],
    ['Extension _locales mismatches', res.extensionMismatches],
  ];
  for (const [title, list] of sections) {
    if (list.length > 0) {
      console.error(`\nFAIL: ${title} (${list.length}):`);
      for (const item of list) console.error(`  - ${item}`);
    }
  }
  if (showUnused && res.unusedInCode.length > 0) {
    console.log(`\nUnused keys in i18n JSON (${res.unusedInCode.length}):`);
    for (const item of res.unusedInCode) console.log(`  - ${item}`);
  }
  if (!res.ok) {
    process.exit(1);
  }
  console.log('PASS: All used i18n keys exist in both AR and EN, and all AR/EN files match 1-to-1.');
}

module.exports = { auditI18n, extractUsedI18nKeys };
