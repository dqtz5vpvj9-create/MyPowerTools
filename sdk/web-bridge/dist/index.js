class BrowserMptWebBridge {
    pending = new Map();
    themeListeners = new Set();
    constructor() {
        window.addEventListener("message", event => this.receive(event.data));
        const webview = globalThis.chrome?.webview;
        webview?.addEventListener?.("message", (event) => this.receive(event.data));
    }
    get available() {
        return Boolean(globalThis.chrome?.webview?.postMessage || window.parent !== window);
    }
    invoke(commandId, args = {}) {
        return this.request("command.invoke", { commandId, args });
    }
    getSetting(name) {
        return this.request("settings.get", { name });
    }
    setSetting(name, value) {
        return this.request("settings.set", { name, value });
    }
    getSecret(name) {
        return this.request("secrets.get", { name });
    }
    setSecret(name, value) {
        return this.request("secrets.set", { name, value });
    }
    publish(type, payload) {
        return this.request("event.publish", { type, payload });
    }
    openExternal(url) {
        return this.request("navigation.openExternal", { url });
    }
    onThemeChanged(listener) {
        this.themeListeners.add(listener);
        return () => this.themeListeners.delete(listener);
    }
    request(type, payload) {
        const id = crypto.randomUUID();
        const message = { version: "1.0", id, type, payload };
        return new Promise((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            const webview = globalThis.chrome?.webview;
            if (webview?.postMessage)
                webview.postMessage(message);
            else
                window.parent.postMessage(message, "*");
            setTimeout(() => {
                if (this.pending.delete(id))
                    reject(new Error(`MyPowerTools bridge timeout: ${type}`));
            }, 15000);
        });
    }
    receive(raw) {
        if (!raw || raw.version !== "1.0")
            return;
        if (raw.type === "theme.changed") {
            this.themeListeners.forEach(listener => listener(raw.payload?.theme ?? "system"));
            return;
        }
        const pending = this.pending.get(raw.id);
        if (!pending)
            return;
        this.pending.delete(raw.id);
        if (raw.error)
            pending.reject(new Error(raw.error.message ?? String(raw.error)));
        else
            pending.resolve(raw.payload);
    }
}
export const mpt = new BrowserMptWebBridge();
