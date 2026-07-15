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
    error?: {
        code: string;
        message: string;
        retryable?: boolean;
    };
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
declare class BrowserMptWebBridge implements MptWebBridge {
    private readonly pending;
    private readonly themeListeners;
    constructor();
    get available(): boolean;
    invoke<T>(commandId: string, args?: Record<string, unknown>): Promise<T>;
    getSetting<T>(name: string): Promise<T | undefined>;
    setSetting(name: string, value: unknown): Promise<void>;
    getSecret(name: string): Promise<string | undefined>;
    setSecret(name: string, value: string): Promise<void>;
    publish(type: string, payload?: unknown): Promise<void>;
    openExternal(url: string): Promise<void>;
    onThemeChanged(listener: (theme: MptTheme) => void): () => void;
    private request;
    private receive;
}
export declare const mpt: BrowserMptWebBridge;
export {};
