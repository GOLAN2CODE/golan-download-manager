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
  let cookiesByUrl = options.cookiesByUrl || {};
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
        events.onMessage.emit(msg, { tab: { id: 1, url: "https://example.com/watch" } }, (res) => {
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
        if (Object.prototype.hasOwnProperty.call(cookiesByUrl, details.url)) {
          queueMicrotask(() => cb(structuredClone(cookiesByUrl[details.url])));
          return;
        }
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

  function createContentEnvironment(pageUrl = "https://example.com/watch") {
    const u = new URL(pageUrl);
    const docListeners = new Map();
    let appendedHost = null;
    let shadowRoot = null;
    let videos = [];

    const makeButtonEl = (cls) => ({
      className: cls,
      title: "",
      disabled: false,
      textContent: "",
      style: {},
      classList: {
        classes: new Set([cls]),
        add(c) { this.classes.add(c); },
        remove(c) { this.classes.delete(c); },
        contains(c) { return this.classes.has(c); }
      },
      addEventListener() {}
    });

    const wrapEl = makeButtonEl("wrap");
    const mainBtn = makeButtonEl("main");
    const labelEl = makeButtonEl("label");
    const closeBtn = makeButtonEl("close");

    const documentStub = {
      title: "Test Page",
      fullscreenElement: null,
      documentElement: {
        appendChild(el) {
          appendedHost = el;
          el.isConnected = true;
        }
      },
      createElement() {
        return {
          id: "",
          isConnected: false,
          remove() {
            this.isConnected = false;
          },
          attachShadow() {
            shadowRoot = {
              innerHTML: "",
              querySelector(sel) {
                if (sel === ".wrap") return wrapEl;
                if (sel === ".main") return mainBtn;
                if (sel === ".label") return labelEl;
                if (sel === ".close") return closeBtn;
                return null;
              }
            };
            return shadowRoot;
          }
        };
      },
      querySelectorAll(sel) {
        return sel === "video" ? videos : [];
      },
      addEventListener(type, fn) {
        if (!docListeners.has(type)) docListeners.set(type, []);
        docListeners.get(type).push(fn);
      }
    };

    let checkIntervalFn = null;
    const windowStub = {
      innerWidth: 1280,
      innerHeight: 720,
      location: { hostname: u.hostname, pathname: u.pathname },
      document: documentStub,
      chrome: {
        ...chrome,
        runtime: {
          ...chrome.runtime,
          sendMessage(msg, cb) {
            events.onMessage.emit(msg, { tab: { id: 1, url: pageUrl } }, (res) => {
              if (cb) cb(res);
            });
          }
        }
      },
      addEventListener() {},
      requestAnimationFrame(fn) {
        fn();
      },
      getComputedStyle() {
        return { visibility: "visible", display: "block", opacity: "1" };
      },
      MutationObserver: class {
        observe() {}
      },
      setTimeout: sandbox.setTimeout,
      clearTimeout,
      setInterval: (fn) => {
        checkIntervalFn = fn;
        return 1;
      },
      clearInterval: () => {}
    };
    windowStub.window = windowStub;
    vm.createContext(windowStub);
    vm.runInContext(CONTENT_CODE, windowStub, { filename: "content.js" });
    return {
      wrapEl,
      setVideos(list) {
        videos = list;
      },
      triggerCheck() {
        if (checkIntervalFn) checkIntervalFn();
      },
      dispatchMouseDown(eventInit) {
        const handlers = docListeners.get("mousedown") || [];
        for (const fn of handlers) fn(eventInit);
      }
    };
  }

  return {
    chrome,
    storageData,
    calls,
    events,
    downloadItems,
    flush,
    sendMessage,
    triggerDownload,
    createContentEnvironment
  };
}

test("M1) minSizeKB threshold captures large unlisted files and ignores smaller unlisted files", async () => {
  const h = createHarness({
    settings: {
      intercept: true,
      extensions: ["zip"],
      minSizeKB: 512
    }
  });

  // 200 KB .dat file (< 512 KB, not in extensions) -> ignored
  await h.triggerDownload({
    id: 101,
    url: "https://cdn.example.com/small.dat",
    filename: "small.dat",
    totalBytes: 200 * 1024
  });
  assert.equal(h.calls.paused.length, 0);

  // 600 KB .dat file (>= 512 KB) -> intercepted by size threshold
  await h.triggerDownload({
    id: 102,
    url: "https://cdn.example.com/large.dat",
    filename: "large.dat",
    totalBytes: 600 * 1024
  });
  assert.deepEqual(h.calls.paused, [102]);
  assert.deepEqual(h.calls.cancelled, [102]);
  const dlMsg = h.calls.nativeMessages.find((m) => m.message.action === "download");
  assert.ok(dlMsg);
  assert.equal(dlMsg.message.url, "https://cdn.example.com/large.dat");
  assert.equal(dlMsg.message.fileSize, 600 * 1024);
  assert.equal(dlMsg.message.force, false);
});

test("M2) MIME_EXT fallback and onDeterminingFilename second-chance capture without duplicate native messages", async () => {
  const h = createHarness({
    settings: {
      intercept: true,
      extensions: ["msi", "pdf"],
      minSizeKB: 0
    }
  });

  // 1) Extensionless URL with application/pdf MIME is caught via MIME_EXT on onCreated
  await h.triggerDownload({
    id: 201,
    url: "https://docs.example.com/fetch?doc=42",
    filename: "",
    mime: "application/pdf; charset=binary",
    totalBytes: 4096
  });
  assert.deepEqual(h.calls.paused, [201]);

  // If onDeterminingFilename also fires for id 201, handledIds prevents a second native message
  let suggestCalled = 0;
  h.events.onDeterminingFilename.emit(
    {
      id: 201,
      state: "in_progress",
      url: "https://docs.example.com/fetch?doc=42",
      filename: "C:\\Users\\User\\Downloads\\report.pdf",
      mime: "application/pdf"
    },
    () => {
      suggestCalled++;
    }
  );
  await h.flush();
  // Taken by GDM: the browser never gets a file name, so its copy never shows up (IDM behaviour).
  assert.equal(suggestCalled, 0);
  assert.equal(h.calls.nativeMessages.filter((m) => m.message.action === "download").length, 1);

  // 2) Extensionless URL with generic MIME on onCreated, then .msi revealed on onDeterminingFilename
  await h.triggerDownload({
    id: 202,
    url: "https://releases.example.com/download/latest",
    filename: "",
    mime: "application/octet-stream",
    totalBytes: -1
  });
  assert.deepEqual(h.calls.paused, [201]); // 202 not paused yet

  h.events.onDeterminingFilename.emit(
    {
      id: 202,
      state: "in_progress",
      url: "https://releases.example.com/download/latest",
      filename: "C:\\Users\\User\\Downloads\\setup.msi",
      mime: "application/octet-stream",
      totalBytes: 1048576
    },
    () => {
      suggestCalled++;
    }
  );
  await h.flush();
  assert.equal(suggestCalled, 0);
  assert.deepEqual(h.calls.cancelled, [201, 202]);
  assert.deepEqual(h.calls.paused, [201, 202]);
  const dlMsgs = h.calls.nativeMessages.filter((m) => m.message.action === "download");
  assert.equal(dlMsgs.length, 2);
  assert.equal(dlMsgs[1].message.filename, "setup.msi");
});

test("M3) contextMenu onClicked dispatches gdm-link, gdm-image, gdm-all-links (with path-scoped cookies), and gdm-media", async () => {
  const h = createHarness({
    cookiesByOrigin: {
      "https://files.example.com": [{ name: "sid", value: "f1", domain: "files.example.com", path: "/" }],
      "https://page.example.com": [{ name: "auth", value: "p1", domain: "page.example.com", path: "/" }]
    },
    cookiesByUrl: {
      "https://files.example.com/sub/a.zip": [
        { name: "sid", value: "f1", domain: "files.example.com", path: "/" },
        { name: "sub_tok", value: "s99", domain: "files.example.com", path: "/sub" }
      ]
    },
    tabsById: {
      7: { id: 7, url: "https://page.example.com/gallery", title: "Gallery Page" }
    },
    executeScriptImpl(injection) {
      // Execute the real collectLinksInPage function against a stub DOM
      const fakeDoc = {
        querySelectorAll() {
          return [
            { href: "https://files.example.com/sub/a.zip#section1", textContent: "  File   A  " },
            { href: "https://files.example.com/sub/a.zip#section2", textContent: "Duplicate hash" },
            { href: "mailto:admin@example.com", textContent: "Email" },
            { href: "javascript:void(0)", textContent: "JS" },
            { href: "https://page.example.com/b.pdf", title: "Doc B", textContent: "" }
          ];
        }
      };
      const fn = new vm.Script("(" + injection.func.toString() + ")()");
      const res = fn.runInNewContext({ document: fakeDoc });
      return [{ result: res }];
    }
  });

  const tab = { id: 7, url: "https://page.example.com/gallery", title: "Gallery Page" };

  // 1) gdm-link
  h.events.onMenuClicked.emit(
    { menuItemId: "gdm-link", linkUrl: "https://files.example.com/archive.zip", pageUrl: tab.url },
    tab
  );
  await h.flush();

  // 2) gdm-image
  h.events.onMenuClicked.emit(
    { menuItemId: "gdm-image", srcUrl: "https://files.example.com/photo.png", pageUrl: tab.url },
    tab
  );
  await h.flush();

  // 3) gdm-all-links
  h.events.onMenuClicked.emit({ menuItemId: "gdm-all-links", pageUrl: tab.url }, tab);
  await h.flush();

  // 4) gdm-media
  h.events.onMenuClicked.emit(
    { menuItemId: "gdm-media", srcUrl: "https://files.example.com/clip.mp4", pageUrl: tab.url },
    tab
  );
  await h.flush();

  const actions = h.calls.nativeMessages.map((m) => m.message);
  assert.equal(actions.length, 4);

  assert.equal(actions[0].action, "download");
  assert.equal(actions[0].url, "https://files.example.com/archive.zip");
  assert.equal(actions[0].referrer, tab.url);
  assert.equal(actions[0].cookies, "sid=f1");

  assert.equal(actions[1].action, "download");
  assert.equal(actions[1].url, "https://files.example.com/photo.png");

  assert.equal(actions[2].action, "downloadAll");
  assert.equal(actions[2].links.length, 2);
  assert.deepEqual(actions[2].links[0], {
    url: "https://files.example.com/sub/a.zip",
    text: "File A",
    cookies: "sid=f1; sub_tok=s99"
  });
  assert.deepEqual(actions[2].links[1], {
    url: "https://page.example.com/b.pdf",
    text: "Doc B",
    cookies: "auth=p1"
  });

  assert.equal(actions[3].action, "media");
  assert.equal(actions[3].pageUrl, tab.url);
  assert.equal(actions[3].mediaUrls[0].url, "https://files.example.com/clip.mp4");
});

test("M4) tab media cache deduplicates URLs, caps at MAX_MEDIA_PER_TAB (30), and clears on navigation/remove/replace", async () => {
  const h = createHarness({
    tabsById: {
      11: { id: 11, url: "https://video.example.com/watch?v=1", title: "Watch 1" }
    }
  });
  await h.sendMessage({ type: "get-status" });

  // Duplicate URL updates size & mime without adding a second entry
  h.events.onResponseStarted.emit({
    tabId: 11,
    url: "https://cdn.example.com/v0.mp4",
    responseHeaders: [{ name: "content-type", value: "video/mp4" }]
  });
  h.events.onResponseStarted.emit({
    tabId: 11,
    url: "https://cdn.example.com/v0.mp4",
    responseHeaders: [
      { name: "content-type", value: "video/mp4" },
      { name: "content-range", value: "bytes 0-1023/2097152" }
    ]
  });
  await h.flush();

  let mediaRes = await h.sendMessage({ type: "get-media", tabId: 11 });
  assert.equal(mediaRes.items.length, 1);
  assert.equal(mediaRes.items[0].size, 2097152);

  // Add 34 more unique streams -> capped at 30 with oldest evicted
  for (let i = 1; i <= 34; i++) {
    h.events.onResponseStarted.emit({
      tabId: 11,
      url: `https://cdn.example.com/stream_${i}.m3u8`,
      responseHeaders: [{ name: "content-type", value: "application/vnd.apple.mpegurl" }]
    });
  }
  await h.flush();

  mediaRes = await h.sendMessage({ type: "get-media", tabId: 11 });
  assert.equal(mediaRes.items.length, 30);
  assert.equal(mediaRes.items[0].url, "https://cdn.example.com/stream_5.m3u8");
  assert.equal(mediaRes.items[29].url, "https://cdn.example.com/stream_34.m3u8");

  // Hash-only URL change does NOT clear media
  h.events.onTabUpdated.emit(11, { url: "https://video.example.com/watch?v=1#t=30s" });
  await h.flush();
  mediaRes = await h.sendMessage({ type: "get-media", tabId: 11 });
  assert.equal(mediaRes.items.length, 30);

  // Navigating to a different page clears media and resets badge
  h.events.onTabUpdated.emit(11, { url: "https://video.example.com/watch?v=2" });
  await h.flush();
  mediaRes = await h.sendMessage({ type: "get-media", tabId: 11 });
  assert.equal(mediaRes.items.length, 0);

  // Record again, then trigger onReplaced and onRemoved
  h.events.onResponseStarted.emit({
    tabId: 11,
    url: "https://cdn.example.com/after.mp4",
    responseHeaders: [{ name: "content-type", value: "video/mp4" }, { name: "content-length", value: "900000" }]
  });
  await h.flush();
  assert.equal((await h.sendMessage({ type: "get-media", tabId: 11 })).items.length, 1);

  h.events.onTabReplaced.emit(99, 11);
  await h.flush();
  assert.equal(h.storageData.session.media_11, undefined);

  h.events.onResponseStarted.emit({
    tabId: 11,
    url: "https://cdn.example.com/after2.mp4",
    responseHeaders: [{ name: "content-type", value: "video/mp4" }, { name: "content-length", value: "900000" }]
  });
  await h.flush();
  h.events.onTabRemoved.emit(11);
  await h.flush();
  assert.equal(h.storageData.session.media_11, undefined);
});

test("M5) popup download-item routes HLS/DASH streams to 'media' and direct files to 'download'", async () => {
  const h = createHarness({
    tabsById: {
      5: { id: 5, url: "https://player.example.com/embed", title: "Embed Player" }
    }
  });

  // 1) Stream item (.mpd or stream MIME) -> action: "media" with only that stream URL
  const r1 = await h.sendMessage({
    type: "download-item",
    tabId: 5,
    item: { url: "https://cdn.example.com/manifest.mpd", mime: "application/dash+xml", size: -1 }
  });
  assert.equal(r1.ok, true);

  // 2) Direct video item (.mp4) -> action: "download"
  const r2 = await h.sendMessage({
    type: "download-item",
    tabId: 5,
    item: { url: "https://cdn.example.com/video.mp4", mime: "video/mp4", size: 4194304 }
  });
  assert.equal(r2.ok, true);

  const msgs = h.calls.nativeMessages.map((m) => m.message);
  assert.equal(msgs[0].action, "media");
  assert.equal(msgs[0].pageUrl, "https://player.example.com/embed");
  assert.deepEqual(msgs[0].mediaUrls, [
    { url: "https://cdn.example.com/manifest.mpd", mime: "application/dash+xml", size: -1 }
  ]);

  assert.equal(msgs[1].action, "download");
  assert.equal(msgs[1].url, "https://cdn.example.com/video.mp4");
  assert.equal(msgs[1].referrer, "https://player.example.com/embed");
  assert.equal(msgs[1].fileSize, 4194304);
});

test("M6) panel-settings respects mediaPanel and mediaPanelExceptions and content.js reacts to gdmState storage.onChanged", async () => {
  let currentMediaPanel = true;
  const h = createHarness({
    nativeHandler(host, message) {
      if (message.action === "ping") {
        return {
          ok: true,
          version: "1.2.3",
          settings: {
            mediaPanel: currentMediaPanel,
            mediaPanelExceptions: ["*.internal.local", "noshow.example.com"]
          }
        };
      }
      return { ok: true };
    }
  });

  const allowed = await h.sendMessage(
    { type: "panel-settings" },
    { tab: { id: 1, url: "https://watch.example.com/video" } }
  );
  assert.equal(allowed.enabled, true);

  const blockedSub = await h.sendMessage(
    { type: "panel-settings" },
    { tab: { id: 2, url: "https://portal.internal.local/training" } }
  );
  assert.equal(blockedSub.enabled, false);

  const blockedExact = await h.sendMessage(
    { type: "panel-settings" },
    { tab: { id: 3, url: "https://sub.noshow.example.com/clip" } }
  );
  assert.equal(blockedExact.enabled, false);

  // Verify content.js live reaction to gdmState changes in chrome.storage.onChanged
  const contentEnv = h.createContentEnvironment("https://watch.example.com/video");
  contentEnv.setVideos([
    {
      isConnected: true,
      currentSrc: "https://watch.example.com/clip.mp4",
      getBoundingClientRect() {
        return { top: 20, left: 20, right: 660, bottom: 380, width: 640, height: 360 };
      }
    }
  ]);
  await h.flush();
  contentEnv.triggerCheck();
  await h.flush();
  assert.equal(contentEnv.wrapEl.classList.contains("show"), true);

  // Now update settings in GDM (mediaPanel: false), refresh state, and emit storage.onChanged for gdmState
  currentMediaPanel = false;
  await h.sendMessage({ type: "get-status" });
  h.events.onStorageChanged.emit({ gdmState: { newValue: h.storageData.local.gdmState } }, "local");
  await h.flush();
  assert.equal(
    contentEnv.wrapEl.classList.contains("show"),
    false,
    "content.js must hide video panel when gdmState disables mediaPanel via storage.onChanged"
  );

  // Re-enable mediaPanel: true and emit storage.onChanged -> panel re-appears
  currentMediaPanel = true;
  await h.sendMessage({ type: "get-status" });
  h.events.onStorageChanged.emit({ gdmState: { newValue: h.storageData.local.gdmState } }, "local");
  await h.flush();
  assert.equal(
    contentEnv.wrapEl.classList.contains("show"),
    true,
    "content.js must restore video panel when gdmState re-enables mediaPanel via storage.onChanged"
  );
});

test("M7) [BUG] redirect to an exceptionSite or exceptionAddress in finalUrl should NOT be intercepted, and Alt+Ctrl force forwards force:true", async () => {
  const h = createHarness({
    settings: {
      intercept: true,
      extensions: ["zip"],
      exceptionSites: ["*.internal.local"],
      exceptionAddresses: ["*://cdn.example.com/private/*"]
    }
  });

  // Download starts on a public redirector and lands on an excluded internal host in finalUrl
  await h.triggerDownload({
    id: 701,
    url: "https://go.example.com/redirect?id=701",
    finalUrl: "https://hr.internal.local/payroll.zip",
    filename: "payroll.zip",
    totalBytes: 50000
  });

  // Download starts on a generic URL and redirects to an excluded address pattern in finalUrl
  await h.triggerDownload({
    id: 702,
    url: "https://go.example.com/redirect?id=702",
    finalUrl: "https://cdn.example.com/private/secret.zip",
    filename: "secret.zip",
    totalBytes: 50000
  });

  assert.deepEqual(
    h.calls.paused,
    [],
    "Downloads redirecting to exceptionSites or exceptionAddresses in finalUrl must not be intercepted"
  );

  // Now force-download via link-keys (Alt+Ctrl+Click) on an excluded redirect target -> intercepted with force: true
  await h.sendMessage(
    { type: "link-keys", url: "https://go.example.com/redirect?id=703", prevent: false, force: true },
    { tab: { id: 1, url: "https://go.example.com/" } }
  );
  await h.triggerDownload({
    id: 703,
    url: "https://go.example.com/redirect?id=703",
    finalUrl: "https://hr.internal.local/payroll.zip",
    filename: "payroll.zip",
    totalBytes: 50000
  });
  assert.deepEqual(h.calls.paused, [703]);
  const forcedMsg = h.calls.nativeMessages.find((m) => m.message.action === "download");
  assert.ok(forcedMsg);
  assert.equal(forcedMsg.message.force, true, "forced download must include force: true in native message");
});

test("M8) [BUG] webRequest media capture must enforce mediaPanelMinSizeKB on cold wakeup and respect mediaPanelExceptions / mediaPanel:false", async () => {
  // Case A: Cold wakeup with mediaPanel: false alone (large 1 MB video on allowed host) -> 0 items captured
  const hDisabled = createHarness({
    initialLocal: {
      gdmState: {
        connected: true,
        version: "1.2.3",
        ts: Date.now(),
        settings: {
          intercept: true,
          extensions: ["zip"],
          minSizeKB: 0,
          exceptionSites: [],
          exceptionAddresses: [],
          mediaPanel: false,
          mediaPanelMinSizeKB: 500,
          mediaPanelExceptions: [],
          menuItems: ["link", "all", "video", "image"]
        }
      }
    },
    tabsById: {
      20: { id: 20, url: "https://allowed.example.com/watch", title: "Allowed" }
    }
  });
  hDisabled.events.onResponseStarted.emit({
    tabId: 20,
    url: "https://cdn.example.com/large.mp4",
    responseHeaders: [
      { name: "content-type", value: "video/mp4" },
      { name: "content-length", value: String(1024 * 1024) }
    ]
  });
  await hDisabled.flush();
  assert.equal(
    (await hDisabled.sendMessage({ type: "get-media", tabId: 20 })).items.length,
    0,
    "Cold wakeup with mediaPanel:false must not capture media even when size >= mediaPanelMinSizeKB"
  );

  // Case B & C: Cold wakeup with mediaPanel: true, mediaPanelMinSizeKB: 500, mediaPanelExceptions: ["excluded.example.com"]
  const h = createHarness({
    initialLocal: {
      gdmState: {
        connected: true,
        version: "1.2.3",
        ts: Date.now(),
        settings: {
          intercept: true,
          extensions: ["zip"],
          minSizeKB: 0,
          exceptionSites: [],
          exceptionAddresses: [],
          mediaPanel: true,
          mediaPanelMinSizeKB: 500,
          mediaPanelExceptions: ["excluded.example.com"],
          menuItems: ["link", "all", "video", "image"]
        }
      }
    },
    tabsById: {
      21: { id: 21, url: "https://allowed.example.com/watch", title: "Allowed Tab" },
      22: { id: 22, url: "https://excluded.example.com/page", title: "Excluded Tab" }
    }
  });

  // 1) 100 KB video on allowed tab & allowed CDN (< 500 KB mediaPanelMinSizeKB on cold wakeup) -> ignored
  h.events.onResponseStarted.emit({
    tabId: 21,
    url: "https://cdn.example.com/tiny.mp4",
    responseHeaders: [
      { name: "content-type", value: "video/mp4" },
      { name: "content-length", value: String(100 * 1024) }
    ]
  });

  // 2) 1 MB video hosted on excluded.example.com inside allowed tab -> ignored via media.url exception
  h.events.onResponseStarted.emit({
    tabId: 21,
    url: "https://excluded.example.com/big-excluded-host.mp4",
    responseHeaders: [
      { name: "content-type", value: "video/mp4" },
      { name: "content-length", value: String(1024 * 1024) }
    ]
  });

  // 3) 1 MB video on allowed CDN inside excluded tab (tab 22) -> ignored via tab pageUrl exception
  h.events.onResponseStarted.emit({
    tabId: 22,
    url: "https://cdn.example.com/big-on-excluded-tab.mp4",
    responseHeaders: [
      { name: "content-type", value: "video/mp4" },
      { name: "content-length", value: String(1024 * 1024) }
    ]
  });

  // 4) Positive control: 1 MB video on allowed CDN inside allowed tab (tab 21) -> captured
  h.events.onResponseStarted.emit({
    tabId: 21,
    url: "https://cdn.example.com/big-allowed.mp4",
    responseHeaders: [
      { name: "content-type", value: "video/mp4" },
      { name: "content-length", value: String(1024 * 1024) }
    ]
  });
  await h.flush();

  const tab22Media = await h.sendMessage({ type: "get-media", tabId: 22 });
  assert.equal(tab22Media.items.length, 0, "Media on excluded tab host must not be captured");

  const tab21Media = await h.sendMessage({ type: "get-media", tabId: 21 });
  assert.deepEqual(
    tab21Media.items.map((x) => x.url),
    ["https://cdn.example.com/big-allowed.mp4"],
    "Only allowed media >= mediaPanelMinSizeKB on allowed tab should be captured"
  );
});
