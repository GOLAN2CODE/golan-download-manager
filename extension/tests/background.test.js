const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const BG_PATH = path.resolve(__dirname, "..", "background.js");
const CONTENT_PATH = path.resolve(__dirname, "..", "content.js");
const BG_CODE = fs.readFileSync(BG_PATH, "utf8");
const CONTENT_CODE = fs.readFileSync(CONTENT_PATH, "utf8");

function makeEvent() {
  const listeners = [];
  return {
    listeners,
    addListener(fn, filter, extraInfoSpec) {
      listeners.push({ fn, filter, extraInfoSpec });
    },
    emit(...args) {
      return listeners.map((l) => l.fn(...args));
    }
  };
}

function createHarness(options = {}) {
  const storageData = {
    local: { ...(options.initialLocal || {}) },
    session: { ...(options.initialSession || {}) }
  };

  function makeStorageArea(areaName) {
    return {
      get(keys, cb) {
        const store = storageData[areaName];
        let res = {};
        if (keys === null || keys === undefined) {
          res = { ...store };
        } else if (typeof keys === "string") {
          if (Object.prototype.hasOwnProperty.call(store, keys)) {
            res[keys] = structuredClone(store[keys]);
          }
        } else if (Array.isArray(keys)) {
          for (const k of keys) {
            if (Object.prototype.hasOwnProperty.call(store, k)) {
              res[k] = structuredClone(store[k]);
            }
          }
        } else if (typeof keys === "object") {
          for (const [k, defVal] of Object.entries(keys)) {
            res[k] = Object.prototype.hasOwnProperty.call(store, k)
              ? structuredClone(store[k])
              : defVal;
          }
        }
        queueMicrotask(() => cb(res));
      },
      set(items, cb) {
        Object.assign(storageData[areaName], structuredClone(items));
        queueMicrotask(() => cb && cb());
      },
      remove(keys, cb) {
        const list = Array.isArray(keys) ? keys : [keys];
        for (const k of list) delete storageData[areaName][k];
        queueMicrotask(() => cb && cb());
      }
    };
  }

  const calls = {
    nativeMessages: [],
    paused: [],
    cancelled: [],
    erased: [],
    resumed: [],
    searched: [],
    downloaded: [],
    menusCreated: [],
    menusRemovedCount: 0,
    notifications: [],
    cookiesGetAll: [],
    badges: []
  };

  const downloadItems = new Map();
  let nativeHandler =
    options.nativeHandler ||
    ((host, message) => {
      if (message.action === "ping") {
        return {
          ok: true,
          version: "1.2.3",
          settings: options.settings || {}
        };
      }
      if (message.action === "download") {
        return { ok: true, accepted: true };
      }
      if (message.action === "downloadAll") {
        return { ok: true, accepted: true };
      }
      if (message.action === "media") {
        return { ok: true, accepted: true };
      }
      return { ok: true };
    });

  let cookiesByOrigin = options.cookiesByOrigin || {};
  let scriptLinksByTab = options.scriptLinksByTab || {};
  let tabsById = options.tabsById || {};

  const events = {
    onBeforeRequest: makeEvent(),
    onResponseStarted: makeEvent(),
    onDownloadCreated: makeEvent(),
    onDeterminingFilename: makeEvent(),
    onTabUpdated: makeEvent(),
    onTabRemoved: makeEvent(),
    onTabReplaced: makeEvent(),
    onMenuClicked: makeEvent(),
    onMessage: makeEvent(),
    onInstalled: makeEvent(),
    onStartup: makeEvent(),
    onStorageChanged: makeEvent()
  };

  const chrome = {
    runtime: {
      id: "gdm-test-extension-id",
      lastError: null,
      getURL(p) {
        return `chrome-extension://gdm-test-extension-id/${p}`;
      },
      sendNativeMessage(host, message, cb) {
        calls.nativeMessages.push({ host, message: structuredClone(message) });
        Promise.resolve()
          .then(() => nativeHandler(host, message))
          .then(
            (reply) => {
              if (reply && reply.__unreachable) {
                chrome.runtime.lastError = { message: "Specified native messaging host not found." };
                cb(undefined);
                chrome.runtime.lastError = null;
              } else {
                chrome.runtime.lastError = null;
                cb(reply);
              }
            },
            (err) => {
              chrome.runtime.lastError = { message: String(err && err.message ? err.message : err) };
              cb(undefined);
              chrome.runtime.lastError = null;
            }
          );
      },
      sendMessage(msg, cb) {
        events.onMessage.emit(msg, { tab: { id: 1, url: "https://example.com/" } }, (res) => {
          if (cb) cb(res);
        });
      },
      onMessage: events.onMessage,
      onInstalled: events.onInstalled,
      onStartup: events.onStartup
    },
    storage: {
      local: makeStorageArea("local"),
      session: makeStorageArea("session"),
      onChanged: events.onStorageChanged
    },
    downloads: {
      onCreated: events.onDownloadCreated,
      onDeterminingFilename: events.onDeterminingFilename,
      pause(id, cb) {
        calls.paused.push(id);
        const it = downloadItems.get(id);
        if (it) it.state = "paused";
        queueMicrotask(() => cb && cb());
      },
      cancel(id, cb) {
        calls.cancelled.push(id);
        const it = downloadItems.get(id);
        if (it) it.state = "interrupted";
        queueMicrotask(() => cb && cb());
      },
      erase(query, cb) {
        calls.erased.push(structuredClone(query));
        if (query && typeof query.id === "number") downloadItems.delete(query.id);
        queueMicrotask(() => cb && cb([]));
      },
      resume(id, cb) {
        calls.resumed.push(id);
        const it = downloadItems.get(id);
        if (it) it.state = "in_progress";
        queueMicrotask(() => cb && cb());
      },
      search(query, cb) {
        calls.searched.push(query);
        const it = query && typeof query.id === "number" ? downloadItems.get(query.id) : null;
        queueMicrotask(() => cb && cb(it ? [{ ...it }] : []));
      },
      download(opts, cb) {
        calls.downloaded.push(opts);
        queueMicrotask(() => cb && cb(999));
      }
    },
    webRequest: {
      onBeforeRequest: events.onBeforeRequest,
      onResponseStarted: events.onResponseStarted
    },
    cookies: {
      getAll(details, cb) {
        calls.cookiesGetAll.push(details);
        let origin = "";
        try {
          origin = new URL(details.url).origin;
        } catch (e) {
          origin = "";
        }
        const list = cookiesByOrigin[origin] || [];
        queueMicrotask(() => cb(structuredClone(list)));
      }
    },
    contextMenus: {
      removeAll(cb) {
        calls.menusRemovedCount += 1;
        calls.menusCreated.length = 0;
        queueMicrotask(() => cb && cb());
      },
      create(props, cb) {
        calls.menusCreated.push(structuredClone(props));
        queueMicrotask(() => cb && cb());
      },
      onClicked: events.onMenuClicked
    },
    notifications: {
      create(id, opts, cb) {
        calls.notifications.push({ id, opts });
        queueMicrotask(() => cb && cb(id));
      }
    },
    scripting: {
      executeScript(injection, cb) {
        const tabId = injection && injection.target && injection.target.tabId;
        if (typeof options.executeScriptImpl === "function") {
          const res = options.executeScriptImpl(injection);
          queueMicrotask(() => cb(res));
          return;
        }
        const links = scriptLinksByTab[tabId] || [];
        queueMicrotask(() => cb([{ result: structuredClone(links) }]));
      }
    },
    tabs: {
      get(tabId, cb) {
        const tab = tabsById[tabId];
        if (!tab) {
          chrome.runtime.lastError = { message: "No tab with id" };
          queueMicrotask(() => {
            cb(undefined);
            chrome.runtime.lastError = null;
          });
          return;
        }
        queueMicrotask(() => cb(structuredClone(tab)));
      },
      onUpdated: events.onTabUpdated,
      onRemoved: events.onTabRemoved,
      onReplaced: events.onTabReplaced
    },
    action: {
      setBadgeText(details, cb) {
        calls.badges.push({ type: "text", ...details });
        queueMicrotask(() => cb && cb());
      },
      setBadgeBackgroundColor(details, cb) {
        calls.badges.push({ type: "color", ...details });
        queueMicrotask(() => cb && cb());
      }
    }
  };

  const sandbox = {
    chrome,
    navigator: { userAgent: "GdmTestUA/1.0" },
    URL,
    Date,
    Promise,
    Set,
    Map,
    RegExp,
    JSON,
    String,
    Number,
    Array,
    Object,
    Math,
    isFinite,
    decodeURIComponent,
    console,
    setTimeout: (fn, ms, ...args) => {
      const t = setTimeout(fn, ms, ...args);
      if (t && typeof t.unref === "function") t.unref();
      return t;
    },
    clearTimeout,
    setInterval: () => 0,
    clearInterval: () => {}
  };

  vm.createContext(sandbox);
  vm.runInContext(BG_CODE, sandbox, { filename: "background.js" });

  async function flush(turns = 12) {
    for (let i = 0; i < turns; i++) {
      await new Promise((r) => setImmediate(r));
    }
  }

  function sendMessage(msg, sender = {}) {
    return new Promise((resolve) => {
      events.onMessage.emit(msg, sender, (res) => resolve(structuredClone(res)));
    });
  }

  async function triggerDownload(item) {
    const fullItem = {
      state: "in_progress",
      mime: "application/octet-stream",
      totalBytes: 10240,
      ...item
    };
    downloadItems.set(fullItem.id, { ...fullItem });
    events.onDownloadCreated.emit(fullItem);
    await flush();
  }

  function createContentEnvironment() {
    const docListeners = new Map();
    const documentStub = {
      title: "Test Page",
      fullscreenElement: null,
      documentElement: {
        appendChild() {}
      },
      querySelectorAll() {
        return [];
      },
      addEventListener(type, fn) {
        if (!docListeners.has(type)) docListeners.set(type, []);
        docListeners.get(type).push(fn);
      }
    };
    const windowStub = {
      innerWidth: 1280,
      innerHeight: 720,
      location: { hostname: "example.com", pathname: "/" },
      document: documentStub,
      chrome,
      addEventListener() {},
      requestAnimationFrame() {},
      getComputedStyle() {
        return { visibility: "visible", display: "block", opacity: "1" };
      },
      MutationObserver: class {
        observe() {}
      },
      setTimeout: sandbox.setTimeout,
      clearTimeout,
      setInterval: () => 0,
      clearInterval: () => {}
    };
    windowStub.window = windowStub;
    vm.createContext(windowStub);
    vm.runInContext(CONTENT_CODE, windowStub, { filename: "content.js" });
    return {
      dispatchMouseDown(eventInit) {
        const handlers = docListeners.get("mousedown") || [];
        for (const fn of handlers) fn(eventInit);
      }
    };
  }

  return {
    chrome,
    calls,
    events,
    storageData,
    downloadItems,
    setNativeHandler(fn) {
      nativeHandler = fn;
    },
    setCookiesByOrigin(map) {
      cookiesByOrigin = map;
    },
    setScriptLinksByTab(map) {
      scriptLinksByTab = map;
    },
    setTabsById(map) {
      tabsById = map;
    },
    flush,
    sendMessage,
    triggerDownload,
    createContentEnvironment
  };
}

// ---------------------------------------------------------------------------
// 1) Matching download (.zip) is paused, sent to GDM with ORIGINAL url,
//    then cancelled + erased ONLY when GDM replies accepted:true
// ---------------------------------------------------------------------------
test("1) matching .zip download is paused first, sent with ORIGINAL url, and cancelled+erased only after accepted:true", async () => {
  let resolveDownloadReply;
  const downloadReplyPromise = new Promise((r) => {
    resolveDownloadReply = r;
  });

  const h = createHarness({
    cookiesByOrigin: {
      "https://origin.example.com": [{ name: "sid", value: "abc123" }]
    },
    nativeHandler(host, message) {
      if (message.action === "ping") {
        return { ok: true, version: "1.0.0", settings: {} };
      }
      if (message.action === "download") {
        return downloadReplyPromise;
      }
      return { ok: true };
    }
  });

  const item = {
    id: 101,
    url: "https://origin.example.com/releases/archive.zip?token=orig",
    finalUrl: "https://cdn-mirror.example.net/bucket/archive.zip?sig=temp",
    referrer: "https://origin.example.com/downloads",
    filename: "/Users/test/Downloads/archive.zip",
    totalBytes: 524288,
    mime: "application/zip",
    state: "in_progress"
  };

  h.downloadItems.set(item.id, { ...item });
  h.events.onDownloadCreated.emit(item);
  await h.flush();

  // While waiting for GDM's reply: download MUST be paused, NOT yet cancelled or erased
  assert.deepEqual(h.calls.paused, [101]);
  assert.deepEqual(h.calls.cancelled, []);
  assert.deepEqual(h.calls.erased, []);

  const dlMsg = h.calls.nativeMessages.find((m) => m.message.action === "download");
  assert.ok(dlMsg, "download message should be sent to GDM");
  assert.equal(dlMsg.host, "com.golan.gdm");
  assert.equal(dlMsg.message.url, item.url, "must send ORIGINAL item.url to GDM");
  assert.equal(dlMsg.message.finalUrl, item.finalUrl, "must include finalUrl separately");
  assert.equal(dlMsg.message.filename, "archive.zip");
  assert.equal(dlMsg.message.fileSize, 524288);
  assert.equal(dlMsg.message.cookies, "sid=abc123");

  // Now GDM replies accepted:true -> cancel and erase must happen
  resolveDownloadReply({ ok: true, accepted: true });
  await h.flush();

  assert.deepEqual(h.calls.cancelled, [101]);
  assert.deepEqual(h.calls.erased, [{ id: 101 }]);
  assert.deepEqual(h.calls.resumed, []);
});

// ---------------------------------------------------------------------------
// 2) accepted:false or GDM unreachable -> download is resumed and not
//    captured again (skip list)
// ---------------------------------------------------------------------------
test("2a) accepted:false -> download is resumed, not cancelled/erased, and added to skip list so subsequent download is ignored", async () => {
  const h = createHarness({
    nativeHandler(host, message) {
      if (message.action === "ping") {
        return { ok: true, version: "1.0.0", settings: {} };
      }
      if (message.action === "download") {
        return { ok: true, accepted: false };
      }
      return { ok: true };
    }
  });

  const url = "https://example.com/files/declined.zip";
  const finalUrl = "https://cdn.example.com/files/declined.zip";

  await h.triggerDownload({
    id: 201,
    url,
    finalUrl,
    filename: "declined.zip",
    mime: "application/zip"
  });

  assert.deepEqual(h.calls.paused, [201]);
  assert.deepEqual(h.calls.cancelled, []);
  assert.deepEqual(h.calls.erased, []);
  assert.deepEqual(h.calls.resumed, [201]);

  const downloadCallsBefore = h.calls.nativeMessages.filter((m) => m.message.action === "download").length;
  assert.equal(downloadCallsBefore, 1);

  // Trigger again with a new download ID (both original url and finalUrl must be skipped)
  await h.triggerDownload({
    id: 202,
    url,
    finalUrl,
    filename: "declined.zip",
    mime: "application/zip"
  });
  await h.triggerDownload({
    id: 203,
    url: finalUrl,
    finalUrl,
    filename: "declined.zip",
    mime: "application/zip"
  });

  const downloadCallsAfter = h.calls.nativeMessages.filter((m) => m.message.action === "download").length;
  assert.equal(downloadCallsAfter, 1, "skip list must prevent re-capturing declined download");
  assert.deepEqual(h.calls.paused, [201]);
});

test("2b) GDM unreachable during download -> download is resumed and skipped on retry", async () => {
  let downloadAttempts = 0;
  const h = createHarness({
    nativeHandler(host, message) {
      if (message.action === "ping") {
        return { ok: true, version: "1.0.0", settings: {} };
      }
      if (message.action === "download") {
        downloadAttempts += 1;
        return { __unreachable: true };
      }
      return { ok: true };
    }
  });

  const url = "https://example.com/files/offline.zip";
  await h.triggerDownload({
    id: 204,
    url,
    filename: "offline.zip",
    mime: "application/zip"
  });

  assert.equal(downloadAttempts, 1);
  assert.deepEqual(h.calls.paused, [204]);
  assert.deepEqual(h.calls.cancelled, []);
  assert.deepEqual(h.calls.erased, []);
  assert.deepEqual(h.calls.resumed, [204]);

  // Even if state is refreshed back to connected, the URL is in the skip list
  await h.sendMessage({ type: "get-status" });
  await h.triggerDownload({
    id: 205,
    url,
    filename: "offline.zip",
    mime: "application/zip"
  });

  assert.equal(downloadAttempts, 1, "URL in skip list must not be captured again");
  assert.deepEqual(h.calls.paused, [204]);
});

// ---------------------------------------------------------------------------
// 3) Exception sites/addresses (wildcards) are not captured
// ---------------------------------------------------------------------------
test("3) exceptionSites and exceptionAddresses (including wildcards) are not captured", async () => {
  const h = createHarness({
    settings: {
      exceptionSites: ["ignored.example.com", "*.corp.local"],
      exceptionAddresses: [
        "https://files.example.org/private/*",
        "https://dl.example.org/temp-?.zip"
      ]
    }
  });

  // Prime settings via get-status
  await h.sendMessage({ type: "get-status" });

  // Exact & subdomain match on exceptionSites
  await h.triggerDownload({
    id: 301,
    url: "https://ignored.example.com/file.zip",
    filename: "file.zip"
  });
  await h.triggerDownload({
    id: 302,
    url: "https://sub.ignored.example.com/file.zip",
    filename: "file.zip"
  });
  // Wildcard host match on exceptionSites
  await h.triggerDownload({
    id: 303,
    url: "https://hr.portal.corp.local/payroll.zip",
    filename: "payroll.zip"
  });
  // Wildcard address matches on exceptionAddresses (* and ?)
  await h.triggerDownload({
    id: 304,
    url: "https://files.example.org/private/secret/archive.zip",
    filename: "archive.zip"
  });
  await h.triggerDownload({
    id: 305,
    url: "https://dl.example.org/temp-A.zip",
    filename: "temp-A.zip"
  });

  assert.deepEqual(h.calls.paused, [], "none of the exception URLs should be paused");

  // Non-matching URL on same host outside wildcard path IS captured
  await h.triggerDownload({
    id: 306,
    url: "https://files.example.org/public/archive.zip",
    filename: "archive.zip"
  });
  assert.deepEqual(h.calls.paused, [306]);
});

// ---------------------------------------------------------------------------
// 4) POST-originated downloads are not captured
// ---------------------------------------------------------------------------
test("4) POST-originated downloads are not captured", async () => {
  const h = createHarness();

  const postUrl = "https://example.com/generate-report.zip";
  const getUrl = "https://example.com/normal-report.zip";

  h.events.onBeforeRequest.emit({
    method: "POST",
    url: postUrl,
    type: "main_frame"
  });
  h.events.onBeforeRequest.emit({
    method: "GET",
    url: getUrl,
    type: "main_frame"
  });

  await h.triggerDownload({
    id: 401,
    url: postUrl,
    filename: "generate-report.zip",
    mime: "application/zip"
  });

  assert.deepEqual(h.calls.paused, [], "POST download must not be captured");

  await h.triggerDownload({
    id: 402,
    url: getUrl,
    filename: "normal-report.zip",
    mime: "application/zip"
  });

  assert.deepEqual(h.calls.paused, [402], "GET download should still be captured");
});

// ---------------------------------------------------------------------------
// 5) Alt-click (link-keys prevent) is not captured;
//    Alt+Ctrl (force) is captured even for a non-listed extension
// ---------------------------------------------------------------------------
test("5) Alt-click (prevent) skips capture; Alt+Ctrl (force) captures even a non-listed extension", async () => {
  const h = createHarness({
    settings: {
      extensions: ["zip"],
      exceptionSites: ["excluded.example.com"]
    }
  });
  const contentEnv = h.createContentEnvironment();
  await h.flush();

  const zipUrl = "https://example.com/files/bundle.zip";
  // Simulate Alt-click in content.js (altKey: true, ctrlKey: false, button: 0)
  contentEnv.dispatchMouseDown({
    isTrusted: true,
    altKey: true,
    ctrlKey: false,
    button: 0,
    target: {
      closest(sel) {
        return sel === "a[href]" ? { href: zipUrl } : null;
      }
    }
  });
  await h.flush();

  await h.triggerDownload({
    id: 501,
    url: zipUrl,
    filename: "bundle.zip",
    mime: "application/zip"
  });

  assert.deepEqual(h.calls.paused, [], "Alt-click (prevent:true) must prevent capture of .zip");

  // Simulate Alt+Ctrl-click on a non-listed extension (.customext) and on an exception site
  const customUrl = "https://excluded.example.com/data/notes.customext";
  contentEnv.dispatchMouseDown({
    isTrusted: true,
    altKey: true,
    ctrlKey: true,
    button: 0,
    target: {
      closest(sel) {
        return sel === "a[href]" ? { href: customUrl } : null;
      }
    }
  });
  await h.flush();

  await h.triggerDownload({
    id: 502,
    url: customUrl,
    filename: "notes.customext",
    mime: "text/plain"
  });

  assert.deepEqual(h.calls.paused, [502], "Alt+Ctrl (force:true) must capture non-listed extension");
  assert.deepEqual(h.calls.cancelled, [502]);
  assert.deepEqual(h.calls.erased, [{ id: 502 }]);
});

// ---------------------------------------------------------------------------
// 6) downloadAll sends each link with the cookies of ITS OWN origin only
// ---------------------------------------------------------------------------
test("6) downloadAll sends each link with the cookies of ITS OWN origin only", async () => {
  const h = createHarness({
    tabsById: {
      77: { id: 77, url: "https://page-host.example.com/list", title: "List" }
    },
    scriptLinksByTab: {
      77: [
        { url: "https://page-host.example.com/dl/one.zip", text: "One" },
        { url: "https://cdn-a.other.net/files/two.zip", text: "Two" },
        { url: "https://mirror-b.third.org/pub/three.iso", text: "Three" },
        { url: "https://cdn-a.other.net/files/four.zip", text: "Four" }
      ]
    },
    cookiesByOrigin: {
      "https://page-host.example.com": [{ name: "page_session", value: "SECRET_PAGE" }],
      "https://cdn-a.other.net": [
        { name: "cdn_auth", value: "TOKEN_A" },
        { name: "cf_clearance", value: "CF_1" }
      ],
      "https://mirror-b.third.org": []
    }
  });

  const res = await h.sendMessage({ type: "download-all", tabId: 77 });
  await h.flush();

  assert.equal(res.ok, true);
  assert.equal(res.count, 4);

  const msg = h.calls.nativeMessages.find((m) => m.message.action === "downloadAll");
  assert.ok(msg, "downloadAll native message should be sent");
  assert.equal(msg.message.referrer, "https://page-host.example.com/list");

  const links = msg.message.links;
  assert.equal(links.length, 4);
  assert.equal(links[0].cookies, "page_session=SECRET_PAGE");
  assert.equal(links[1].cookies, "cdn_auth=TOKEN_A; cf_clearance=CF_1");
  assert.equal(links[2].cookies, "");
  assert.equal(links[3].cookies, "cdn_auth=TOKEN_A; cf_clearance=CF_1");

  // Ensure cdn-a.other.net cookie query was only performed once due to origin caching
  const cdnCookieQueries = h.calls.cookiesGetAll.filter((q) => q.url === "https://cdn-a.other.net/");
  assert.equal(cdnCookieQueries.length, 1);
});

// ---------------------------------------------------------------------------
// 7) context menus follow settings.menuItems
// ---------------------------------------------------------------------------
test("7) context menus follow settings.menuItems on refresh", async () => {
  let currentMenuItems = ["link", "video"];
  const h = createHarness({
    nativeHandler(host, message) {
      if (message.action === "ping") {
        return {
          ok: true,
          version: "1.0.0",
          settings: {
            menuItems: currentMenuItems
          }
        };
      }
      return { ok: true };
    }
  });

  await h.sendMessage({ type: "get-status" });
  await h.flush();

  assert.deepEqual(
    h.calls.menusCreated.map((m) => m.id),
    ["gdm-link", "gdm-media"]
  );

  // Change menuItems in GDM settings and refresh
  currentMenuItems = ["all", "image"];
  await h.sendMessage({ type: "get-status" });
  await h.flush();

  assert.deepEqual(
    h.calls.menusCreated.map((m) => m.id),
    ["gdm-all-links", "gdm-image"]
  );

  // Empty menuItems disables all context menus
  currentMenuItems = [];
  await h.sendMessage({ type: "get-status" });
  await h.flush();

  assert.deepEqual(h.calls.menusCreated, []);
});

// ---------------------------------------------------------------------------
// 8) media smaller than mediaPanelMinSizeKB is not recorded;
//    googlevideo.com and .ts segments are ignored
// ---------------------------------------------------------------------------
test("8) media smaller than mediaPanelMinSizeKB is ignored, googlevideo.com and .ts segments are ignored", async () => {
  const h = createHarness({
    tabsById: {
      12: { id: 12, url: "https://watch.example.com/video/1", title: "Watch" }
    },
    settings: {
      mediaPanel: true,
      mediaPanelMinSizeKB: 500 // 512,000 bytes minimum
    }
  });

  // Populate gdmState.settings via get-status
  await h.sendMessage({ type: "get-status" });
  await h.flush();

  // 8a) Media smaller than 500 KB (100 KB via Content-Length) -> ignored
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://cdn.example.com/media/small-preview.mp4",
    responseHeaders: [
      { name: "Content-Type", value: "video/mp4" },
      { name: "Content-Length", value: String(100 * 1024) }
    ]
  });

  // 8b) Media smaller than 500 KB via Content-Range total -> ignored
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://cdn.example.com/media/small-range.mp4",
    responseHeaders: [
      { name: "Content-Type", value: "video/mp4" },
      { name: "Content-Range", value: "bytes 0-1023/204800" }
    ]
  });

  // 8c) googlevideo.com and *.googlevideo.com -> ignored even when large
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://rr2---sn-5hne6n7k.googlevideo.com/videoplayback?id=123",
    responseHeaders: [
      { name: "Content-Type", value: "video/mp4" },
      { name: "Content-Length", value: String(10 * 1024 * 1024) }
    ]
  });
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://googlevideo.com/videoplayback.mp4",
    responseHeaders: [
      { name: "Content-Type", value: "video/mp4" },
      { name: "Content-Length", value: String(10 * 1024 * 1024) }
    ]
  });

  // 8d) .ts segments (by extension and by video/mp2t mime) -> ignored even when large
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://cdn.example.com/hls/segment-0001.ts",
    responseHeaders: [
      { name: "Content-Type", value: "video/mp2t" },
      { name: "Content-Length", value: String(2 * 1024 * 1024) }
    ]
  });
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://cdn.example.com/hls/segment-0002.ts",
    responseHeaders: [
      { name: "Content-Type", value: "video/mp4" },
      { name: "Content-Length", value: String(2 * 1024 * 1024) }
    ]
  });

  // 8e) Valid media >= 500 KB (2 MB) and valid HLS master manifest (.m3u8) -> recorded
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://cdn.example.com/media/full-movie.mp4",
    responseHeaders: [
      { name: "Content-Type", value: "video/mp4" },
      { name: "Content-Length", value: String(2 * 1024 * 1024) }
    ]
  });
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://cdn.example.com/hls/master.m3u8",
    responseHeaders: [
      { name: "Content-Type", value: "application/vnd.apple.mpegurl" }
    ]
  });

  await h.flush();

  const mediaRes = await h.sendMessage({ type: "get-media", tabId: 12 });
  assert.deepEqual(
    mediaRes.items.map((x) => x.url),
    [
      "https://cdn.example.com/media/full-movie.mp4",
      "https://cdn.example.com/hls/master.m3u8"
    ]
  );
  assert.equal(mediaRes.items[0].size, 2 * 1024 * 1024);
});

// ---------------------------------------------------------------------------
// 9) Manifest supports Chrome, Edge, and Firefox + i18n + F-10 least privilege
// ---------------------------------------------------------------------------
test("9) manifest.json has service_worker + scripts, gecko settings, Chromium key, i18n placeholders, and http/https host_permissions", () => {
  const manifestPath = path.resolve(__dirname, "..", "manifest.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));

  assert.equal(manifest.manifest_version, 3);
  assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
  assert.equal(manifest.default_locale, "en");
  assert.match(manifest.name, /^__MSG_.*__$/);
  assert.match(manifest.description, /^__MSG_.*__$/);
  assert.ok(manifest.key && manifest.key.length > 100, "Chromium key must be preserved");
  assert.equal(manifest.background.service_worker, "background.js");
  assert.deepEqual(manifest.background.scripts, ["background.js"]);
  assert.equal(manifest.browser_specific_settings?.gecko?.id, "gdm@golan.local");
  assert.ok(
    parseFloat(manifest.browser_specific_settings?.gecko?.strict_min_version || "0") >= 109,
    "gecko.strict_min_version must support MV3 + nativeMessaging + downloads"
  );
  assert.deepEqual(manifest.host_permissions, ["http://*/*", "https://*/*"]);
});

// ---------------------------------------------------------------------------
// 10) Firefox WebExtensions (globalThis.browser promise style + missing APIs)
// ---------------------------------------------------------------------------
test("10) works in Firefox environment (globalThis.browser promises, no downloads.onDeterminingFilename)", async () => {
  const h = createHarness();
  const onCreated = makeEvent();
  const paused = [];
  const cancelled = [];
  const erased = [];

  const browserMock = {
    runtime: {
      id: "gdm@golan.local",
      lastError: null,
      getURL: (p) => `moz-extension://uuid/${p}`,
      sendNativeMessage: (host, message) =>
        Promise.resolve(
          message.action === "ping"
            ? { ok: true, version: "1.1.0", settings: {} }
            : { ok: true, accepted: true }
        ),
      onMessage: makeEvent(),
      onInstalled: makeEvent(),
      onStartup: makeEvent()
    },
    storage: {
      local: {
        get: () => Promise.resolve({}),
        set: () => Promise.resolve(),
        remove: () => Promise.resolve()
      },
      session: {
        get: () => Promise.resolve({}),
        set: () => Promise.resolve(),
        remove: () => Promise.resolve()
      }
    },
    downloads: {
      onCreated,
      // No onDeterminingFilename in Firefox!
      pause: (id) => {
        paused.push(id);
        return Promise.resolve();
      },
      cancel: (id) => {
        cancelled.push(id);
        return Promise.resolve();
      },
      erase: (q) => {
        erased.push(structuredClone(q));
        return Promise.resolve([]);
      },
      resume: () => Promise.resolve(),
      search: () => Promise.resolve([]),
      download: () => Promise.resolve(1)
    },
    webRequest: {
      onBeforeRequest: makeEvent(),
      onResponseStarted: makeEvent()
    },
    cookies: {
      getAll: () => Promise.resolve([{ name: "ff", value: "1" }])
    },
    contextMenus: {
      removeAll: () => Promise.resolve(),
      create: () => {},
      onClicked: makeEvent()
    },
    notifications: {
      create: () => Promise.resolve("id")
    },
    tabs: {
      get: (id) => Promise.resolve({ id, url: "https://example.org/page", title: "FF" }),
      executeScript: () => Promise.resolve([[{ url: "https://example.org/a.zip", text: "A" }]]),
      onUpdated: makeEvent(),
      onRemoved: makeEvent(),
      onReplaced: makeEvent()
    },
    action: {
      setBadgeText: () => Promise.resolve(),
      setBadgeBackgroundColor: () => Promise.resolve()
    },
    i18n: {
      getMessage: (k) => k,
      getUILanguage: () => "ar"
    }
  };

  const sandbox = {
    browser: browserMock,
    chrome: browserMock,
    navigator: { userAgent: "Mozilla/5.0 Firefox/128.0" },
    URL,
    Date,
    Promise,
    Set,
    Map,
    RegExp,
    JSON,
    String,
    Number,
    Array,
    Object,
    Math,
    isFinite,
    decodeURIComponent,
    console,
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval: () => 0,
    clearInterval: () => {}
  };
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(BG_CODE, sandbox, { filename: "background.js" });

  onCreated.emit({
    id: 901,
    url: "https://example.org/file.zip",
    filename: "file.zip",
    mime: "application/zip",
    totalBytes: 4096,
    state: "in_progress"
  });
  await h.flush();

  assert.deepEqual(paused, [901]);
  assert.deepEqual(cancelled, [901]);
  assert.deepEqual(erased, [{ id: 901 }]);
});

// ---------------------------------------------------------------------------
// 11) F-08 Security: untrusted synthetic mousedown is ignored in content.js;
//     popup-only messages from content scripts are rejected in background.js
// ---------------------------------------------------------------------------
test("11) F-08: untrusted mousedown is ignored and content script cannot invoke popup-only handlers", async () => {
  const h = createHarness({
    settings: {
      extensions: ["zip"]
    },
    tabsById: {
      1: { id: 1, url: "https://example.com/", title: "Page" }
    },
    scriptLinksByTab: {
      1: [{ url: "https://example.com/a.zip", text: "A" }]
    }
  });
  const contentEnv = h.createContentEnvironment();
  await h.flush();

  // Synthetic untrusted Alt+Ctrl click attempting to force-download a non-listed extension
  const targetUrl = "https://example.com/internal-endpoint.html";
  contentEnv.dispatchMouseDown({
    isTrusted: false,
    altKey: true,
    ctrlKey: true,
    button: 0,
    target: {
      closest(sel) {
        return sel === "a[href]" ? { href: targetUrl } : null;
      }
    }
  });
  await h.flush();

  await h.triggerDownload({
    id: 1101,
    url: targetUrl,
    filename: "internal-endpoint.html",
    mime: "text/html"
  });
  assert.deepEqual(h.calls.paused, [], "untrusted mousedown must not set force flag");

  // Content script (sender.tab present) attempting to invoke popup-only messages
  const senderWithTab = { tab: { id: 1, url: "https://example.com/" } };
  for (const type of ["get-status", "get-media", "download-item", "media-page", "download-all"]) {
    const res = await h.sendMessage({ type, tabId: 1, item: { url: "https://example.com/a.zip" } }, senderWithTab);
    assert.equal(res?.ok, false, `popup-only message ${type} must be rejected when sender.tab is present`);
  }
});

// ---------------------------------------------------------------------------
// 12) i18n locales (ar & en) and popup RTL/LTR direction switching
// ---------------------------------------------------------------------------
test("12) i18n messages.json exist for ar and en in Modern Standard Arabic, and popup sets dir=rtl for ar and dir=ltr for en", async () => {
  const enPath = path.resolve(__dirname, "..", "_locales", "en", "messages.json");
  const arPath = path.resolve(__dirname, "..", "_locales", "ar", "messages.json");
  assert.ok(fs.existsSync(enPath), "_locales/en/messages.json must exist");
  assert.ok(fs.existsSync(arPath), "_locales/ar/messages.json must exist");

  const en = JSON.parse(fs.readFileSync(enPath, "utf8"));
  const ar = JSON.parse(fs.readFileSync(arPath, "utf8"));
  assert.deepEqual(Object.keys(ar).sort(), Object.keys(en).sort(), "ar and en must have identical message keys");

  assert.equal(en.extName.message, "Golan Download Manager");
  assert.equal(ar.extName.message, "جولان - مدير التنزيل");

  for (const [k, v] of Object.entries(ar)) {
    assert.ok(v && typeof v.message === "string" && v.message.length > 0, `ar.${k} must have a message`);
    assert.doesNotMatch(v.message, /آدم/, `ar.${k} must not contain old name آدم: ${v.message}`);
    assert.doesNotMatch(
      v.message,
      /\b(مو|هلق|هلأ|بدك|شو|بيلتقط|بيبعتها|شغّال|انبعت)\b/,
      `ar.${k} must be Modern Standard Arabic, not dialect: ${v.message}`
    );
  }
  const legacyEnRe = new RegExp("\\b" + "\x41dam" + "\\b", "i");
  for (const [k, v] of Object.entries(en)) {
    assert.doesNotMatch(v.message, legacyEnRe, `en.${k} must not contain legacy name: ${v.message}`);
  }

  // Verify popup.js applies dir="rtl" for Arabic and dir="ltr" for English
  const popupCode = fs.readFileSync(path.resolve(__dirname, "..", "popup.js"), "utf8");
  for (const [uiLang, expectedDir] of [
    ["ar", "rtl"],
    ["en-US", "ltr"]
  ]) {
    const table = uiLang.startsWith("ar") ? ar : en;
    const elements = new Map();
    const makeEl = (id = "") => ({
      id,
      textContent: "",
      className: "",
      hidden: false,
      disabled: false,
      checked: true,
      dataset: {},
      style: {},
      append() {},
      appendChild() {},
      addEventListener() {},
      getAttribute: () => null
    });
    const doc = {
      title: "",
      documentElement: { lang: "", dir: "" },
      body: { dir: "" },
      getElementById(id) {
        if (!elements.has(id)) elements.set(id, makeEl(id));
        return elements.get(id);
      },
      querySelectorAll() {
        return [];
      },
      createElement() {
        return makeEl();
      },
      createTextNode(t) {
        return { textContent: t };
      }
    };
    const chromeStub = {
      runtime: {
        lastError: null,
        sendMessage(_msg, cb) {
          if (cb) queueMicrotask(() => cb({ connected: true, items: [] }));
          return Promise.resolve({ connected: true, items: [] });
        }
      },
      tabs: {
        query(_q, cb) {
          const tabs = [{ id: 1, url: "https://example.com/", title: "Ex" }];
          if (cb) queueMicrotask(() => cb(tabs));
          return Promise.resolve(tabs);
        }
      },
      storage: {
        local: {
          get(_k, cb) {
            if (cb) queueMicrotask(() => cb({}));
            return Promise.resolve({});
          },
          set(_v, cb) {
            if (cb) queueMicrotask(() => cb());
            return Promise.resolve();
          }
        }
      },
      i18n: {
        getUILanguage: () => uiLang,
        getMessage(key, subs) {
          const entry = table[key];
          if (!entry) return "";
          let out = entry.message;
          const list = Array.isArray(subs) ? subs : subs !== undefined ? [subs] : [];
          list.forEach((s, i) => {
            out = out.replace(new RegExp("\\$" + (i + 1), "g"), String(s));
          });
          return out;
        }
      }
    };
    const popupSandbox = {
      chrome: chromeStub,
      browser: chromeStub,
      document: doc,
      navigator: { language: uiLang },
      URL,
      Promise,
      Array,
      Object,
      String,
      Number,
      decodeURIComponent,
      setTimeout: () => 0,
      clearTimeout: () => {}
    };
    popupSandbox.globalThis = popupSandbox;
    vm.createContext(popupSandbox);
    vm.runInContext(popupCode, popupSandbox, { filename: "popup.js" });
    await new Promise((r) => setImmediate(r));
    await new Promise((r) => setImmediate(r));
    assert.equal(doc.documentElement.dir, expectedDir, `popup dir for ${uiLang} must be ${expectedDir}`);
  }
});

// ---------------------------------------------------------------------------
// 13) All i18n keys referenced in popup.html, popup.js, content.js, and
//     background.js exist in both _locales/en/messages.json and _locales/ar/messages.json
// ---------------------------------------------------------------------------
test("13) every i18n key used in popup.html, popup.js, content.js, and background.js exists in en and ar locales", () => {
  const en = JSON.parse(fs.readFileSync(path.resolve(__dirname, "..", "_locales", "en", "messages.json"), "utf8"));
  const ar = JSON.parse(fs.readFileSync(path.resolve(__dirname, "..", "_locales", "ar", "messages.json"), "utf8"));
  const popupHtml = fs.readFileSync(path.resolve(__dirname, "..", "popup.html"), "utf8");
  const popupJs = fs.readFileSync(path.resolve(__dirname, "..", "popup.js"), "utf8");

  const usedKeys = new Set();
  for (const m of popupHtml.matchAll(/data-i18n="([^"]+)"/g)) usedKeys.add(m[1]);
  for (const src of [BG_CODE, CONTENT_CODE, popupJs]) {
    for (const m of src.matchAll(/i18nMsg\(\s*"([^"]+)"/g)) usedKeys.add(m[1]);
  }

  assert.ok(usedKeys.size >= 25, "should extract all i18n keys from extension files");
  for (const key of usedKeys) {
    assert.ok(en[key] && en[key].message, `missing key "${key}" in _locales/en/messages.json`);
    assert.ok(ar[key] && ar[key].message, `missing key "${key}" in _locales/ar/messages.json`);
  }
});

// ---------------------------------------------------------------------------
// 14) F-04 / F-05 Cookie sanitization strips CR, LF, and TAB in cookieHeader
//     and cookieObjectsForApp
// ---------------------------------------------------------------------------
test("14) F-04/F-05: cookieHeader and cookieObjectsForApp strip CR, LF, and TAB characters", async () => {
  const h = createHarness({
    tabsById: {
      55: { id: 55, url: "https://media.example.com/watch", title: "Watch" }
    },
    cookiesByOrigin: {
      "https://media.example.com": [
        {
          name: "sess\r\n\tid",
          value: "val\r\n\t123",
          domain: ".media.example.com\r\n\t",
          path: "/watch\t\r\n",
          secure: true,
          httpOnly: true,
          hostOnly: false,
          expirationDate: 1800000000,
          session: false
        }
      ]
    }
  });

  await h.triggerDownload({
    id: 1401,
    url: "https://media.example.com/file.zip",
    filename: "file.zip",
    mime: "application/zip"
  });

  const dlMsg = h.calls.nativeMessages.find((m) => m.message.action === "download");
  assert.equal(dlMsg.message.cookies, "sessid=val123");

  await h.sendMessage({ type: "media-page", tabId: 55, title: "Watch" });
  const mediaMsg = h.calls.nativeMessages.find((m) => m.message.action === "media");
  assert.equal(mediaMsg.message.cookies.length, 1);
  assert.deepEqual(mediaMsg.message.cookies[0], {
    name: "sessid",
    value: "val123",
    domain: ".media.example.com",
    path: "/watch",
    secure: true,
    httpOnly: true,
    hostOnly: false,
    expirationDate: 1800000000,
    session: false
  });
});

// ---------------------------------------------------------------------------
// 15) F-08 foreign sender.id rejection + allowed content-script messages,
//     F-10 webRequest URL filter check, and F-15 wildcard asterisk collapsing
// ---------------------------------------------------------------------------
test("15) F-08 sender.id check, F-10 webRequest http/https filters, and F-15 consecutive wildcard collapsing", async () => {
  const h = createHarness({
    settings: {
      exceptionAddresses: ["https://files.example.org/private/**********/*.zip"],
      mediaPanel: true,
      mediaPanelExceptions: ["blocked.example.com"]
    }
  });

  // F-10: webRequest listeners must only subscribe to http://*/* and https://*/*
  assert.deepEqual(structuredClone(h.events.onBeforeRequest.listeners[0].filter.urls), ["http://*/*", "https://*/*"]);
  assert.deepEqual(structuredClone(h.events.onResponseStarted.listeners[0].filter.urls), ["http://*/*", "https://*/*"]);

  // F-08: foreign extension sender.id is rejected even without sender.tab
  const foreignRes = await h.sendMessage({ type: "get-status" }, { id: "malicious-other-extension-id" });
  assert.deepEqual(structuredClone(foreignRes), { ok: false, error: "forbidden-sender" });

  // F-08: allowed content-script message (panel-settings) succeeds with sender.tab
  await h.sendMessage({ type: "get-status" });
  const panelAllowed = await h.sendMessage(
    { type: "panel-settings" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://allowed.example.com/video" } }
  );
  assert.equal(panelAllowed.enabled, true);

  const panelBlocked = await h.sendMessage(
    { type: "panel-settings" },
    { id: "gdm-test-extension-id", tab: { id: 1, url: "https://sub.blocked.example.com/video" } }
  );
  assert.equal(panelBlocked.enabled, false);

  // F-15: repeated asterisks in exceptionAddresses match properly without ReDoS
  await h.triggerDownload({
    id: 1501,
    url: "https://files.example.org/private/a/b/c/secret.zip",
    filename: "secret.zip"
  });
  assert.deepEqual(h.calls.paused, []);
});

// ---------------------------------------------------------------------------
// 16) Extension independence: new RSA-2048 manifest key & GDM-only identifiers
// ---------------------------------------------------------------------------
test("16) manifest key derives expected GDM Chromium extension ID and extension files have zero legacy identifiers", () => {
  const crypto = require("node:crypto");
  const manifest = JSON.parse(fs.readFileSync(path.resolve(__dirname, "..", "manifest.json"), "utf8"));
  assert.equal(typeof manifest.key, "string");
  const spki = Buffer.from(manifest.key, "base64");
  const pubKey = crypto.createPublicKey({ key: spki, format: "der", type: "spki" });
  assert.equal(pubKey.asymmetricKeyType, "rsa");
  assert.equal(pubKey.asymmetricKeyDetails.modulusLength, 2048);

  const hex = crypto.createHash("sha256").update(spki).digest("hex").slice(0, 32);
  const extId = [...hex].map((c) => String.fromCharCode("a".charCodeAt(0) + parseInt(c, 16))).join("");
  assert.equal(extId, "fkjplbaglahdiphiodkgidkdfkbebgfh");
  assert.equal(manifest.browser_specific_settings?.gecko?.id, "gdm@golan.local");

  const legacyRe = new RegExp("\x61dam", "i");
  for (const rel of [
    "manifest.json",
    "background.js",
    "content.js",
    "content.css",
    "popup.js",
    "popup.html",
    "_locales/en/messages.json",
    "_locales/ar/messages.json"
  ]) {
    const text = fs.readFileSync(path.resolve(__dirname, "..", rel), "utf8");
    assert.doesNotMatch(text, legacyRe, `${rel} must not contain legacy identifier`);
  }
  assert.match(BG_CODE, /\bsendToGdm\b/);
  assert.match(BG_CODE, /\bgdmState\b/);
  assert.match(BG_CODE, /\bgetGdmState\b/);
  assert.match(CONTENT_CODE, /\b__gdmContentLoaded\b/);
  assert.match(CONTENT_CODE, /\bgdm-dl-host\b/);
});

// ---------------------------------------------------------------------------
// 17-21) IDM-style hold: the browser copy never shows up while Golan decides
// ---------------------------------------------------------------------------
async function warmHarness(options = {}) {
  const h = createHarness(options);
  // A first ping fills the in-memory settings (what a running extension already has).
  await h.sendMessage({ type: "get-status" });
  await h.flush();
  return h;
}

function determining(h, item) {
  let suggested = 0;
  const results = h.events.onDeterminingFilename.emit(item, () => {
    suggested += 1;
  });
  return { held: results[0] === true, suggested: () => suggested };
}

test("17) a matching download is paused in the same tick, held out of sight, then cancelled when Golan takes it", async () => {
  let answer;
  const reply = new Promise((r) => (answer = r));
  const h = await warmHarness({
    nativeHandler(host, message) {
      if (message.action === "ping") return { ok: true, version: "2.4.0", settings: {} };
      if (message.action === "download") return reply;
      return { ok: true };
    }
  });
  const item = { id: 301, url: "https://download.example.com/AnyDesk.exe", state: "in_progress", mime: "application/octet-stream", filename: "" };
  h.downloadItems.set(item.id, { ...item });
  h.events.onDownloadCreated.emit(item);
  assert.deepEqual(h.calls.paused, [301], "paused synchronously, before any storage read or message");

  const d = determining(h, { ...item, filename: "C:\\Users\\U\\Downloads\\AnyDesk.exe", totalBytes: 8553912 });
  assert.equal(d.held, true, "file name step is held so the browser does not show the download");
  await h.flush();
  assert.deepEqual(h.calls.cancelled, []);

  answer({ ok: true, accepted: true });
  await h.flush();
  assert.deepEqual(h.calls.cancelled, [301]);
  assert.deepEqual(h.calls.erased, [{ id: 301 }]);
  assert.equal(d.suggested(), 0, "a taken download never gets a name, so it never appears in the browser");
  assert.deepEqual(h.calls.resumed, []);
  assert.deepEqual(h.calls.paused, [301], "paused only once");
});

test("18) declined in Golan: the held download gets its name back and continues in the browser", async () => {
  const h = await warmHarness({
    nativeHandler(host, message) {
      if (message.action === "ping") return { ok: true, version: "2.4.0", settings: {} };
      if (message.action === "download") return { ok: true, accepted: false };
      return { ok: true };
    }
  });
  const item = { id: 302, url: "https://files.example.com/a.zip", state: "in_progress", mime: "application/zip", filename: "" };
  h.downloadItems.set(item.id, { ...item });
  h.events.onDownloadCreated.emit(item);
  const d = determining(h, { ...item, filename: "C:\\D\\a.zip" });
  assert.equal(d.held, true);
  await h.flush();
  assert.equal(d.suggested(), 1, "name accepted once");
  assert.deepEqual(h.calls.resumed, [302]);
  assert.deepEqual(h.calls.cancelled, []);
  assert.deepEqual(h.calls.downloaded, [], "the same browser download continues; no second copy");
});

test("19) not for Golan: no pause and the file name is accepted at once", async () => {
  const h = await warmHarness({ settings: { extensions: ["zip"], minSizeKB: 0 } });
  const item = { id: 303, url: "https://example.com/page.html", state: "in_progress", mime: "text/html", filename: "" };
  h.downloadItems.set(item.id, { ...item });
  h.events.onDownloadCreated.emit(item);
  const d = determining(h, { ...item, filename: "C:\\D\\page.html" });
  assert.equal(d.held, false);
  assert.equal(d.suggested(), 1);
  await h.flush();
  assert.deepEqual(h.calls.paused, []);
  assert.equal(h.calls.nativeMessages.filter((m) => m.message.action === "download").length, 0);
});

test("20) settings not known yet: the name step waits for the real decision instead of showing the download", async () => {
  const h = createHarness({ settings: { exceptionSites: ["skip.example.com"] } });
  const item = { id: 304, url: "https://skip.example.com/tool.exe", state: "in_progress", mime: "application/octet-stream", filename: "" };
  h.downloadItems.set(item.id, { ...item });
  const d = determining(h, { ...item, filename: "C:\\D\\tool.exe" });
  assert.equal(d.held, true, "held while the settings are fetched");
  assert.equal(d.suggested(), 0);
  await h.flush();
  assert.equal(d.suggested(), 1, "an exception site goes back to the browser");
  assert.deepEqual(h.calls.cancelled, []);
});

function makePortHarness(reply) {
  const h = createHarness();
  const posted = [];
  const ports = [];
  h.chrome.runtime.connectNative = (host) => {
    const port = {
      host,
      onMessage: makeEvent(),
      onDisconnect: makeEvent(),
      postMessage(msg) {
        posted.push(structuredClone(msg));
        reply(port, structuredClone(msg));
      },
      disconnect() {}
    };
    ports.push(port);
    return port;
  };
  return { h, posted, ports };
}

test("21) one open native port carries the messages and matches replies by request number, even out of order", async () => {
  const pending = [];
  const { h, posted, ports } = makePortHarness((port, msg) => pending.push({ port, msg }));
  const a = h.sendMessage({ type: "get-status" });
  await h.flush();
  const download = { id: 305, url: "https://files.example.com/b.zip", state: "in_progress", mime: "application/zip", filename: "b.zip" };
  h.downloadItems.set(download.id, { ...download });
  h.events.onDownloadCreated.emit(download);
  await h.flush();
  // Answer whatever is waiting in reverse order: request numbers keep them apart.
  for (let round = 0; round < 6 && (pending.length || h.calls.cancelled.length === 0); round++) {
    const batch = pending.splice(0).reverse();
    for (const { port, msg } of batch) {
      const body = msg.action === "ping" ? { ok: true, version: "2.4.0", settings: {} } : { ok: true, accepted: true };
      port.onMessage.emit({ ...body, _rid: msg._rid });
    }
    await h.flush();
  }
  const status = await a;
  assert.equal(status.connected, true);
  assert.equal(ports.length, 1, "a single port is reused");
  assert.equal(h.calls.nativeMessages.length, 0, "no one-shot helper process was needed");
  assert.ok(posted.every((m) => typeof m._rid === "number"));
  assert.ok(posted.some((m) => m.action === "download" && m.url === download.url));
  assert.deepEqual(h.calls.cancelled, [305]);
});

test("22) a refused port falls back to one-shot messages; an older helper without request numbers still works", async () => {
  const refused = makePortHarness((port) => {
    refused.h.chrome.runtime.lastError = { message: "Specified native messaging host not found." };
    port.onDisconnect.emit(port);
    refused.h.chrome.runtime.lastError = null;
  });
  const st = await refused.h.sendMessage({ type: "get-status" });
  assert.equal(st.connected, true, "answered by the one-shot fallback");
  assert.ok(refused.h.calls.nativeMessages.some((m) => m.message.action === "ping"));

  const old = makePortHarness((port, msg) => {
    queueMicrotask(() => port.onMessage.emit({ ok: true, version: "2.3.0", settings: {} })); // no _rid
  });
  const st2 = await old.h.sendMessage({ type: "get-status" });
  assert.equal(st2.connected, true);
  assert.equal(st2.version, "2.3.0");
  assert.equal(old.h.calls.nativeMessages.length, 0);
});
