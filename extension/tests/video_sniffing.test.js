const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const EXT_DIR = path.resolve(__dirname, "..");
const BG_CODE = fs.readFileSync(path.join(EXT_DIR, "background.js"), "utf8");
const CONTENT_CODE = fs.readFileSync(path.join(EXT_DIR, "content.js"), "utf8");
const MANIFEST = JSON.parse(fs.readFileSync(path.join(EXT_DIR, "manifest.json"), "utf8"));

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

function createMockDomElement(tagName, ownerDoc) {
  const classes = new Set();
  const listeners = new Map();
  const children = [];
  let html = "";
  let shadowRoot = null;

  const el = {
    tagName: String(tagName || "div").toUpperCase(),
    id: "",
    title: "",
    textContent: "",
    disabled: false,
    offsetWidth: 168,
    parentNode: null,
    style: { top: "", left: "", visibility: "visible", display: "block", opacity: "1" },
    classList: {
      add(...names) {
        for (const n of names) if (n) classes.add(n);
      },
      remove(...names) {
        for (const n of names) classes.delete(n);
      },
      contains(n) {
        return classes.has(n);
      }
    },
    get isConnected() {
      let cur = el;
      while (cur) {
        if (cur === ownerDoc.documentElement || cur === ownerDoc.body) return true;
        cur = cur.parentNode;
      }
      return false;
    },
    appendChild(child) {
      child.parentNode = el;
      children.push(child);
      return child;
    },
    remove() {
      if (!el.parentNode) return;
      const idx = el.parentNode._children.indexOf(el);
      if (idx >= 0) el.parentNode._children.splice(idx, 1);
      el.parentNode = null;
    },
    attachShadow(init) {
      const sr = createMockShadowRoot(ownerDoc, init && init.mode);
      if (init && init.mode === "open") shadowRoot = sr;
      el._closedShadow = sr;
      return sr;
    },
    get shadowRoot() {
      return shadowRoot;
    },
    set shadowRoot(v) {
      shadowRoot = v;
    },
    addEventListener(type, fn) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(fn);
    },
    dispatchEvent(type, eventObj) {
      for (const fn of listeners.get(type) || []) fn(eventObj);
    },
    getBoundingClientRect() {
      return el._rect || { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 };
    },
    querySelectorAll(sel) {
      return queryMockTree(children, sel);
    },
    querySelector(sel) {
      return queryMockTree(children, sel)[0] || null;
    },
    get innerHTML() {
      return html;
    },
    set innerHTML(v) {
      html = String(v || "");
    },
    _children: children,
    _classes: classes
  };
  return el;
}

function createMockShadowRoot(ownerDoc, mode) {
  let html = "";
  const children = [];
  const sr = {
    mode: mode || "open",
    _children: children,
    appendChild(child) {
      children.push(child);
      return child;
    },
    get innerHTML() {
      return html;
    },
    set innerHTML(v) {
      html = String(v || "");
      children.length = 0;
      if (html.includes('class="wrap"')) {
        const wrap = createMockDomElement("div", ownerDoc);
        wrap.classList.add("wrap");
        const mainBtn = createMockDomElement("button", ownerDoc);
        mainBtn.classList.add("main");
        const labelSpan = createMockDomElement("span", ownerDoc);
        labelSpan.classList.add("label");
        mainBtn.appendChild(labelSpan);
        const closeBtn = createMockDomElement("button", ownerDoc);
        closeBtn.classList.add("close");
        wrap.appendChild(mainBtn);
        wrap.appendChild(closeBtn);
        children.push(wrap);
      }
    },
    querySelectorAll(sel) {
      return queryMockTree(children, sel);
    },
    querySelector(sel) {
      return queryMockTree(children, sel)[0] || null;
    }
  };
  return sr;
}

function matchesMockSelector(el, sel) {
  if (sel === "*") return true;
  if (sel.startsWith(".")) return el.classList.contains(sel.slice(1));
  if (sel.startsWith("#")) return el.id === sel.slice(1);
  return el.tagName.toLowerCase() === sel.toLowerCase();
}

function queryMockTree(nodes, sel, out = []) {
  for (const n of nodes) {
    if (matchesMockSelector(n, sel)) out.push(n);
    if (n._children && n._children.length > 0) queryMockTree(n._children, sel, out);
  }
  return out;
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
          if (Object.prototype.hasOwnProperty.call(store, keys)) res[keys] = structuredClone(store[keys]);
        } else if (Array.isArray(keys)) {
          for (const k of keys) {
            if (Object.prototype.hasOwnProperty.call(store, k)) res[k] = structuredClone(store[k]);
          }
        } else if (typeof keys === "object") {
          for (const [k, defVal] of Object.entries(keys)) {
            res[k] = Object.prototype.hasOwnProperty.call(store, k) ? structuredClone(store[k]) : defVal;
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
    notifications: [],
    cookiesGetAll: [],
    badges: []
  };

  let nativeHandler =
    options.nativeHandler ||
    ((host, message) => {
      if (message.action === "ping") {
        return { ok: true, version: "2.0.0", settings: options.settings || {} };
      }
      return { ok: true, accepted: true };
    });

  let cookiesByOrigin = options.cookiesByOrigin || {};
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
          .then((reply) => {
            chrome.runtime.lastError = null;
            cb(reply);
          });
      },
      sendMessage(msg, cb) {
        const senderTab = options.defaultSenderTab || { id: 1, url: "https://player.site.test/watch/1", title: "Watch 1" };
        events.onMessage.emit(msg, { tab: senderTab }, (res) => cb && cb(res));
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
      pause(id, cb) { queueMicrotask(() => cb && cb()); },
      cancel(id, cb) { queueMicrotask(() => cb && cb()); },
      erase(q, cb) { queueMicrotask(() => cb && cb([])); },
      resume(id, cb) { queueMicrotask(() => cb && cb()); },
      search(q, cb) { queueMicrotask(() => cb && cb([])); },
      download(o, cb) { queueMicrotask(() => cb && cb(1)); }
    },
    webRequest: {
      onBeforeRequest: events.onBeforeRequest,
      onResponseStarted: events.onResponseStarted
    },
    cookies: {
      getAll(details, cb) {
        calls.cookiesGetAll.push(details);
        let origin = "";
        try { origin = new URL(details.url).origin; } catch (e) { origin = ""; }
        queueMicrotask(() => cb(structuredClone(cookiesByOrigin[origin] || [])));
      }
    },
    contextMenus: {
      removeAll(cb) { queueMicrotask(() => cb && cb()); },
      create(props, cb) { queueMicrotask(() => cb && cb()); },
      onClicked: events.onMenuClicked
    },
    notifications: {
      create(id, opts, cb) {
        calls.notifications.push({ id, opts });
        queueMicrotask(() => cb && cb(id));
      }
    },
    tabs: {
      get(tabId, cb) {
        const tab = tabsById[tabId];
        queueMicrotask(() => cb(tab ? structuredClone(tab) : undefined));
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
    navigator: { userAgent: "GdmSniffUA/2.0", language: "en-US" },
    URL,
    Date,
    Promise,
    Set,
    Map,
    WeakSet,
    RegExp,
    JSON,
    String,
    Number,
    Array,
    Object,
    Math,
    Boolean,
    isFinite,
    encodeURIComponent,
    decodeURIComponent,
    console,
    setTimeout: (fn, ms, ...args) => {
      const t = setTimeout(fn, Math.min(ms || 0, 10), ...args);
      if (t && typeof t.unref === "function") t.unref();
      return t;
    },
    clearTimeout,
    setInterval: () => 0,
    clearInterval: () => {}
  };

  vm.createContext(sandbox);
  vm.runInContext(BG_CODE, sandbox, { filename: "background.js" });

  async function flush(turns = 16) {
    for (let i = 0; i < turns; i++) {
      await new Promise((r) => setImmediate(r));
    }
  }

  function sendMessage(msg, sender = {}) {
    return new Promise((resolve) => {
      events.onMessage.emit(msg, sender, (res) => resolve(structuredClone(res)));
    });
  }

  function mountContentPage(pageOpts = {}) {
    const pageUrl = new URL(pageOpts.url || "https://player.site.test/watch/1");
    const senderTab = pageOpts.tab || { id: 1, url: pageUrl.href, title: pageOpts.title || "Video Page" };
    const docListeners = new Map();

    const docStub = {
      title: pageOpts.title || "Video Page",
      fullscreenElement: null,
      documentElement: null,
      body: null,
      createElement(tag) {
        return createMockDomElement(tag, docStub);
      },
      querySelectorAll(sel) {
        return queryMockTree([docStub.documentElement], sel);
      },
      querySelector(sel) {
        return queryMockTree([docStub.documentElement], sel)[0] || null;
      },
      addEventListener(type, fn) {
        if (!docListeners.has(type)) docListeners.set(type, []);
        docListeners.get(type).push(fn);
      },
      dispatchDocumentEvent(type, ev) {
        for (const fn of docListeners.get(type) || []) fn(ev);
      }
    };
    docStub.documentElement = createMockDomElement("html", docStub);
    docStub.documentElement.parentNode = null;
    docStub.body = createMockDomElement("body", docStub);
    docStub.documentElement.appendChild(docStub.body);

    if (typeof pageOpts.setupDom === "function") {
      pageOpts.setupDom(docStub);
    }

    const frameChrome = {
      ...chrome,
      runtime: {
        ...chrome.runtime,
        sendMessage(msg, cb) {
          events.onMessage.emit(msg, { tab: senderTab }, (res) => cb && cb(res));
        }
      }
    };

    const winStub = {
      innerWidth: 1280,
      innerHeight: 720,
      location: {
        href: pageUrl.href,
        hostname: pageUrl.hostname,
        pathname: pageUrl.pathname
      },
      document: docStub,
      navigator: { language: "en-US" },
      chrome: frameChrome,
      WeakSet,
      Number,
      Math,
      Promise,
      addEventListener() {},
      requestAnimationFrame(fn) {
        fn();
      },
      getComputedStyle(el) {
        return el.style || { visibility: "visible", display: "block", opacity: "1" };
      },
      MutationObserver: class {
        observe() {}
      },
      setTimeout: (fn) => {
        // Keep post-click 2500ms reset from wiping the status label immediately during assertions
        return 0;
      },
      clearTimeout,
      setInterval: () => 0,
      clearInterval: () => {}
    };
    winStub.window = winStub;
    winStub.top = pageOpts.isIframe ? { location: { href: senderTab.url } } : winStub;

    vm.createContext(winStub);
    vm.runInContext(CONTENT_CODE, winStub, { filename: "content.js" });

    return {
      doc: docStub,
      win: winStub,
      getHost() {
        return docStub.querySelector("#gdm-dl-host");
      },
      getShadowParts() {
        const host = docStub.querySelector("#gdm-dl-host");
        if (!host || !host._closedShadow) return null;
        return {
          wrap: host._closedShadow.querySelector(".wrap"),
          mainBtn: host._closedShadow.querySelector(".main"),
          label: host._closedShadow.querySelector(".label"),
          closeBtn: host._closedShadow.querySelector(".close")
        };
      }
    };
  }

  return {
    calls,
    events,
    flush,
    sendMessage,
    mountContentPage
  };
}

// ---------------------------------------------------------------------------
// 1) Sniffing .m3u8 / .mpd / query-param manifests / progressive media per tab
//    while ignoring segments (.ts, .m4s, /seg-1.mp4, /init.mp4) & googlevideo
// ---------------------------------------------------------------------------
test("VS-01: webRequest sniffs m3u8, mpd, query-param manifests & progressive media per tab while filtering segments", async () => {
  const h = createHarness({
    tabsById: {
      10: { id: 10, url: "https://cinema.example.test/movie/alpha", title: "Alpha" },
      20: { id: 20, url: "https://cinema.example.test/movie/beta", title: "Beta" }
    }
  });
  await h.flush();

  const emitResp = (tabId, url, contentType, contentLength = "") => {
    const responseHeaders = [{ name: "Content-Type", value: contentType }];
    if (contentLength) responseHeaders.push({ name: "Content-Length", value: String(contentLength) });
    h.events.onResponseStarted.emit({ tabId, url, responseHeaders });
  };

  // Valid streams & progressive media on tab 10
  emitResp(10, "https://cdn.example.test/streams/master.m3u8", "application/vnd.apple.mpegurl");
  emitResp(10, "https://cdn.example.test/api/play?stream=variant_720p.m3u8&sig=xyz", "application/octet-stream");
  emitResp(10, "https://cdn.example.test/dash/manifest.mpd", "application/dash+xml");
  emitResp(10, "https://cdn.example.test/files/full-episode.mp4", "video/mp4", 45_000_000);

  // Segments & YouTube CDN noise on tab 10 (must all be ignored)
  emitResp(10, "https://cdn.example.test/streams/seg-0001.ts", "video/mp2t", 800_000);
  emitResp(10, "https://cdn.example.test/dash/chunk-stream0-00001.m4s", "video/iso.segment", 900_000);
  emitResp(10, "https://cdn.example.test/dash/seg-4.mp4", "video/mp4", 950_000);
  emitResp(10, "https://cdn.example.test/dash/init.mp4", "video/mp4", 4096);
  emitResp(10, "https://rr2---sn-abc.googlevideo.com/videoplayback?mime=video/mp4", "video/mp4", 12_000_000);

  // Separate tab 20 stream
  emitResp(20, "https://cdn.example.test/other/beta-1080p.webm", "video/webm", 25_000_000);

  await h.flush();

  const tab10 = await h.sendMessage({ type: "get-media", tabId: 10 });
  const tab20 = await h.sendMessage({ type: "get-media", tabId: 20 });

  assert.deepEqual(
    tab10.items.map((x) => x.url),
    [
      "https://cdn.example.test/streams/master.m3u8",
      "https://cdn.example.test/api/play?stream=variant_720p.m3u8&sig=xyz",
      "https://cdn.example.test/dash/manifest.mpd",
      "https://cdn.example.test/files/full-episode.mp4"
    ]
  );
  assert.deepEqual(tab20.items.map((x) => x.url), ["https://cdn.example.test/other/beta-1080p.webm"]);

  // Hash-only navigation keeps sniffed streams; real navigation clears them
  h.events.onTabUpdated.emit(10, { url: "https://cinema.example.test/movie/alpha#t=120" });
  await h.flush();
  assert.equal((await h.sendMessage({ type: "get-media", tabId: 10 })).items.length, 4);

  h.events.onTabUpdated.emit(10, { url: "https://cinema.example.test/movie/gamma" });
  await h.flush();
  assert.equal((await h.sendMessage({ type: "get-media", tabId: 10 })).items.length, 0);
});

// ---------------------------------------------------------------------------
// 2) Best-quality ranking, cache-buster deduplication & cross-origin CDN cookies
// ---------------------------------------------------------------------------
test("VS-02: sendMedia deduplicates cache-buster queries, ranks master > 1080p > 720p > 360p > progressive, and merges CDN cookies", async () => {
  const h = createHarness({
    tabsById: {
      5: { id: 5, url: "https://watch.portal.test/v/777", title: "Episode 7" }
    },
    cookiesByOrigin: {
      "https://watch.portal.test": [
        { name: "portal_sid", value: "p123", domain: ".portal.test", path: "/", secure: true, httpOnly: true }
      ],
      "https://edge.cdn-video.test": [
        { name: "CloudFront-Key", value: "cf999", domain: "edge.cdn-video.test", path: "/", secure: true, httpOnly: false }
      ]
    }
  });
  await h.flush();

  const emitResp = (url, mime, size = 0) => {
    const responseHeaders = [{ name: "Content-Type", value: mime }];
    if (size > 0) responseHeaders.push({ name: "Content-Length", value: String(size) });
    h.events.onResponseStarted.emit({ tabId: 5, url, responseHeaders });
  };

  emitResp("https://edge.cdn-video.test/vod/360p.m3u8?token=keep&_ts=1001", "application/vnd.apple.mpegurl");
  emitResp("https://edge.cdn-video.test/vod/720p.m3u8?token=keep", "application/vnd.apple.mpegurl");
  emitResp("https://edge.cdn-video.test/vod/1080p.m3u8?token=keep&rnd=aaa", "application/vnd.apple.mpegurl");
  emitResp("https://edge.cdn-video.test/vod/1080p.m3u8?token=keep&rnd=bbb", "application/vnd.apple.mpegurl"); // duplicate of 1080p
  emitResp("https://edge.cdn-video.test/vod/master.m3u8?token=keep", "application/vnd.apple.mpegurl");
  emitResp("https://edge.cdn-video.test/vod/preview_480p.mp4", "video/mp4", 8 * 1024 * 1024);

  await h.flush();
  h.calls.nativeMessages.length = 0;

  const res = await h.sendMessage({ type: "media-page", tabId: 5, title: "Episode 7" });
  assert.equal(res.ok, true);
  assert.equal(h.calls.nativeMessages.length, 1);

  const msg = h.calls.nativeMessages[0].message;
  assert.equal(msg.action, "media");
  assert.equal(msg.pageUrl, "https://watch.portal.test/v/777");
  assert.equal(msg.mediaUrls.length, 5, "duplicate 1080p cache-busted request must be collapsed");
  assert.equal(msg.mediaUrls[0].url, "https://edge.cdn-video.test/vod/master.m3u8?token=keep");
  assert.match(msg.mediaUrls[1].url, /1080p\.m3u8/);
  assert.match(msg.mediaUrls[2].url, /720p\.m3u8/);
  assert.match(msg.mediaUrls[3].url, /360p\.m3u8/);
  assert.match(msg.mediaUrls[4].url, /preview_480p\.mp4/);

  const cookieNames = msg.cookies.map((c) => c.name).sort();
  assert.deepEqual(cookieNames, ["CloudFront-Key", "portal_sid"]);
});

// ---------------------------------------------------------------------------
// 3) Floating "Download this video" panel over blob:/MediaSource & Shadow DOM
// ---------------------------------------------------------------------------
test("VS-03: content.js shows floating button over blob:/MSE <video> inside open Shadow DOM and sends sniffed HLS stream on click", async () => {
  const tab = { id: 12, url: "https://streambox.example.test/embed/42", title: "Blob Stream Video" };
  const h = createHarness({
    tabsById: { 12: tab }
  });
  await h.flush();

  // Browser sniffs the real HLS manifest behind the MediaSource blob: player
  h.events.onResponseStarted.emit({
    tabId: 12,
    url: "https://hls.streambox.example.test/live-vod/master.m3u8",
    responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }]
  });
  await h.flush();

  const page = h.mountContentPage({
    url: tab.url,
    title: tab.title,
    tab,
    setupDom(doc) {
      // Tiny thumbnail <video> that must be ignored (< 200x120)
      const thumb = doc.createElement("video");
      thumb.src = "https://streambox.example.test/thumb.mp4";
      thumb._rect = { top: 10, left: 10, right: 130, bottom: 80, width: 120, height: 70 };
      doc.body.appendChild(thumb);

      // Custom web-component player with an open Shadow DOM hosting a blob: MSE <video>
      const playerHost = doc.createElement("vjs-shadow-player");
      const openShadow = playerHost.attachShadow({ mode: "open" });
      const blobVideo = doc.createElement("video");
      blobVideo.src = "blob:https://streambox.example.test/9a8b7c6d-1234-5678-9abc-def012345678";
      blobVideo.currentSrc = blobVideo.src;
      blobVideo._rect = { top: 60, left: 100, right: 900, bottom: 510, width: 800, height: 450 };
      // Connect shadow video so targetVideo.isConnected is true
      doc.body.appendChild(playerHost);
      Object.defineProperty(blobVideo, "isConnected", { get: () => true });
      openShadow.appendChild(blobVideo);
    }
  });
  await h.flush();

  const parts = page.getShadowParts();
  assert.ok(parts, "gdm-dl-host closed shadow button must be created");
  assert.equal(parts.wrap.classList.contains("show"), true, "button must be visible over the blob: player");
  assert.match(parts.label.textContent, /Download this video/);

  h.calls.nativeMessages.length = 0;
  parts.mainBtn.dispatchEvent("click", {
    isTrusted: true,
    preventDefault() {},
    stopPropagation() {}
  });
  await h.flush();

  assert.equal(h.calls.nativeMessages.length, 1);
  const nativeMsg = h.calls.nativeMessages[0].message;
  assert.equal(nativeMsg.action, "media");
  assert.equal(nativeMsg.pageUrl, tab.url);
  assert.equal(
    nativeMsg.mediaUrls.some((m) => m.url.startsWith("blob:")),
    false,
    "blob: pseudo-URL must never be forwarded to GDM"
  );
  assert.equal(nativeMsg.mediaUrls[0].url, "https://hls.streambox.example.test/live-vod/master.m3u8");
  assert.match(parts.label.textContent, /Sent to Golan/);
});

// ---------------------------------------------------------------------------
// 4) Embedded <iframe> player: manifest all_frames + frameUrl referrer & cookies
// ---------------------------------------------------------------------------
test("VS-04: manifest enables all_frames and embedded <iframe> video click forwards frameUrl referrer + iframe cookies", async () => {
  assert.equal(MANIFEST.content_scripts[0].all_frames, true, "manifest.json must run content.js in iframes");

  const topTab = { id: 18, url: "https://blog.article.test/post/how-to-cook", title: "Article With Embed" };
  const iframeUrl = "https://player.embedhost.test/embed/video-900";
  const h = createHarness({
    tabsById: { 18: topTab },
    cookiesByOrigin: {
      "https://blog.article.test": [{ name: "blog_pref", value: "1", domain: "blog.article.test", path: "/" }],
      "https://player.embedhost.test": [{ name: "embed_auth", value: "tok_xyz", domain: "player.embedhost.test", path: "/" }]
    }
  });
  await h.flush();

  h.events.onResponseStarted.emit({
    tabId: 18,
    url: "https://player.embedhost.test/hls/master.m3u8",
    responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }]
  });
  await h.flush();

  const iframePage = h.mountContentPage({
    url: iframeUrl,
    title: "Embedded Video Title",
    tab: topTab,
    isIframe: true,
    setupDom(doc) {
      const v = doc.createElement("video");
      v.src = "blob:https://player.embedhost.test/11111111-2222-3333-4444-555555555555";
      v.currentSrc = v.src;
      v._rect = { top: 0, left: 0, right: 640, bottom: 360, width: 640, height: 360 };
      doc.body.appendChild(v);
    }
  });
  await h.flush();

  h.calls.nativeMessages.length = 0;
  const parts = iframePage.getShadowParts();
  assert.ok(parts && parts.wrap.classList.contains("show"));

  parts.mainBtn.dispatchEvent("click", {
    isTrusted: true,
    preventDefault() {},
    stopPropagation() {}
  });
  await h.flush();

  assert.equal(h.calls.nativeMessages.length, 1);
  const nativeMsg = h.calls.nativeMessages[0].message;
  assert.equal(nativeMsg.action, "media");
  assert.equal(nativeMsg.pageUrl, topTab.url);
  assert.equal(nativeMsg.referrer, iframeUrl);
  assert.deepEqual(
    nativeMsg.cookies.map((c) => c.name).sort(),
    ["blog_pref", "embed_auth"]
  );
});

// ---------------------------------------------------------------------------
// 5) DRM detection (EME mediaKeys / Widevine license URL) + ftp/ftps/data links
// ---------------------------------------------------------------------------
test("VS-05: DRM streams (EME mediaKeys & Widevine license URL) are clearly refused without contacting GDM, while ftp/ftps/data links forward cleanly", async () => {
  const drmTab = { id: 31, url: "https://stream.cinema.test/watch/protected", title: "Protected Movie" };
  const h = createHarness({
    tabsById: { 31: drmTab }
  });
  await h.flush();

  let videoEl;
  const page = h.mountContentPage({
    url: drmTab.url,
    title: drmTab.title,
    tab: drmTab,
    setupDom(doc) {
      videoEl = doc.createElement("video");
      videoEl.src = "blob:https://stream.cinema.test/eme-video";
      videoEl.mediaKeys = { keySystem: "com.widevine.alpha" };
      videoEl._rect = { top: 20, left: 20, right: 820, bottom: 470, width: 800, height: 450 };
      doc.body.appendChild(videoEl);
    }
  });
  await h.flush();

  h.calls.nativeMessages.length = 0;
  h.calls.notifications.length = 0;
  const parts = page.getShadowParts();
  parts.mainBtn.dispatchEvent("click", {
    isTrusted: true,
    preventDefault() {},
    stopPropagation() {}
  });
  await h.flush();

  assert.equal(h.calls.nativeMessages.length, 0, "DRM stream must NOT be dispatched for download");
  assert.equal(parts.mainBtn.classList.contains("err"), true);
  assert.match(parts.label.textContent, /Protected videos \(DRM\) cannot be downloaded/i);
  assert.equal(h.calls.notifications.length, 1);

  // Also test Widevine license URL sniffed via webRequest when video.mediaKeys wasn't set
  const licTab = { id: 32, url: "https://vod.provider.test/play/55", title: "Widevine Title" };
  h.events.onResponseStarted.emit({
    tabId: 32,
    url: "https://drm.provider.test/widevine/license?session=abc",
    responseHeaders: [{ name: "Content-Type", value: "application/octet-stream" }]
  });
  await h.flush();

  const licReply = await h.sendMessage(
    { type: "media-from-page", title: "Widevine Title", videoSrc: "", frameUrl: "", drmProtected: false },
    { tab: licTab }
  );
  assert.equal(licReply.ok, false);
  assert.equal(licReply.error, "drm");
  assert.equal(h.calls.nativeMessages.length, 0);

  // Verify ftp://, ftps://, and data: links via context menu gdm-link forward to GDM
  for (const linkUrl of [
    "ftp://mirror.example.test/pub/release.tar.gz",
    "ftps://mirror.example.test/secure/archive.zip",
    "data:text/plain;base64,SGVsbG8gR29sYW4h"
  ]) {
    h.events.onMenuClicked.emit(
      { menuItemId: "gdm-link", linkUrl, pageUrl: "https://mirror.example.test/" },
      { id: 31, url: "https://mirror.example.test/" }
    );
  }
  await h.flush();

  assert.equal(h.calls.nativeMessages.length, 3);
  assert.deepEqual(
    h.calls.nativeMessages.map((m) => m.message.url),
    [
      "ftp://mirror.example.test/pub/release.tar.gz",
      "ftps://mirror.example.test/secure/archive.zip",
      "data:text/plain;base64,SGVsbG8gR29sYW4h"
    ]
  );
});
