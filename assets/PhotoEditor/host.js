// Gallery bridge. The original editor algorithms and UI are unchanged.
window.hvcEvents = new Proxy({}, { get: (_target, name) => (...args) => {
    chrome.webview.postMessage({name, args});
}});
