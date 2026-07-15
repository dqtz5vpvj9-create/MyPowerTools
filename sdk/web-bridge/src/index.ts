export type MptTheme = "system" | "light" | "dark";

export interface MptHostMessage<T = unknown> {
  version: "1.0";
  id: string;
  type: string;
  payload?: T;
}

export interface MptCommandResult {
  state: string;
  success: boolean;
  output?: string;
  error?: { code: string; message: string; retryable?: boolean };
}

export interface MptWebBridge {
  readonly available: boolean;
  invoke<T = unknown>(commandId: string, args?: Record<string, unknown>): Promise<T>;
  getSetting<T = unknown>(name: string): Promise<T | undefined>;
  setSetting(name: string, value: unknown): Promise<void>;
  getSecret(name: string): Promise<string | undefined>;
  setSecret(name: string, value: string): Promise<void>;
  publish(type: string, payload?: unknown): Promise<void>;
  openExternal(url: string): Promise<void>;
  onThemeChanged(listener: (theme: MptTheme) => void): () => void;
}

type Pending = { resolve(value: unknown): void; reject(reason: unknown): void };

class BrowserMptWebBridge implements MptWebBridge {
  private readonly pending = new Map<string, Pending>();
  private readonly themeListeners = new Set<(theme: MptTheme) => void>();

  constructor() {
    window.addEventListener("message", event => this.receive(event.data));
    const webview = (globalThis as any).chrome?.webview;
    webview?.addEventListener?.("message", (event: MessageEvent) => this.receive(event.data));
  }

  get available(): boolean {
    return Boolean((globalThis as any).chrome?.webview?.postMessage || window.parent !== window);
  }

  invoke<T>(commandId: string, args: Record<string, unknown> = {}): Promise<T> {
    return this.request<T>("command.invoke", { commandId, args });
  }

  getSetting<T>(name: string): Promise<T | undefined> {
    return this.request<T | undefined>("settings.get", { name });
  }

  setSetting(name: string, value: unknown): Promise<void> {
    return this.request<void>("settings.set", { name, value });
  }

  getSecret(name: string): Promise<string | undefined> {
    return this.request<string | undefined>("secrets.get", { name });
  }

  setSecret(name: string, value: string): Promise<void> {
    return this.request<void>("secrets.set", { name, value });
  }

  publish(type: string, payload?: unknown): Promise<void> {
    return this.request<void>("event.publish", { type, payload });
  }

  openExternal(url: string): Promise<void> {
    return this.request<void>("navigation.openExternal", { url });
  }

  onThemeChanged(listener: (theme: MptTheme) => void): () => void {
    this.themeListeners.add(listener);
    return () => this.themeListeners.delete(listener);
  }

  private request<T>(type: string, payload?: unknown): Promise<T> {
    const id = crypto.randomUUID();
    const message: MptHostMessage = { version: "1.0", id, type, payload };
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      const webview = (globalThis as any).chrome?.webview;
      if (webview?.postMessage) webview.postMessage(message);
      else window.parent.postMessage(message, "*");
      setTimeout(() => {
        if (this.pending.delete(id)) reject(new Error(`MyPowerTools bridge timeout: ${type}`));
      }, 15000);
    });
  }

  private receive(raw: any): void {
    if (!raw || raw.version !== "1.0") return;
    if (raw.type === "theme.changed") {
      this.themeListeners.forEach(listener => listener(raw.payload?.theme ?? "system"));
      return;
    }
    const pending = this.pending.get(raw.id);
    if (!pending) return;
    this.pending.delete(raw.id);
    if (raw.error) pending.reject(new Error(raw.error.message ?? String(raw.error)));
    else pending.resolve(raw.payload);
  }
}

export const mpt = new BrowserMptWebBridge();
