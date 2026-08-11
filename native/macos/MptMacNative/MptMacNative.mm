#import <Cocoa/Cocoa.h>
#import <Security/Security.h>
#import <UserNotifications/UserNotifications.h>
#import <WebKit/WebKit.h>

#include <cstdlib>
#include <cstring>
#include <climits>
#include <cmath>

typedef void (*MptWebViewCallback)(void *context, int eventKind, const char *payload);
typedef void (*MptTrayActionCallback)(void *context, const char *actionId);

enum MptWebViewEventKind {
    MptWebViewEventLoading = 0,
    MptWebViewEventReady = 1,
    MptWebViewEventFailed = 2,
    MptWebViewEventBridgeRequest = 3,
    MptWebViewEventShortcut = 4,
    MptWebViewEventFocusMove = 5
};

static NSString *MptString(const char *value) {
    if (value == nullptr) {
        return @"";
    }
    NSString *result = [NSString stringWithUTF8String:value];
    return result ?: @"";
}

static void MptEmit(MptWebViewCallback callback, void *context, int kind, NSString *payload) {
    if (callback == nullptr) {
        return;
    }
    callback(context, kind, (payload ?: @"").UTF8String);
}

static NSArray<NSURL *> *MptParseOrigins(NSString *json) {
    NSData *data = [json dataUsingEncoding:NSUTF8StringEncoding];
    if (data.length == 0) {
        return @[];
    }
    id value = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    if (![value isKindOfClass:NSArray.class]) {
        return @[];
    }
    NSMutableArray<NSURL *> *origins = [NSMutableArray array];
    for (id item in (NSArray *)value) {
        if (![item isKindOfClass:NSString.class]) {
            continue;
        }
        NSURL *url = [NSURL URLWithString:(NSString *)item];
        if (url != nil) {
            [origins addObject:url];
        }
    }
    return origins;
}

@interface MPTWebViewHost : NSView <WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler>
@property(nonatomic, strong) WKWebView *webView;
@property(nonatomic, copy) NSArray<NSURL *> *allowedOrigins;
@property(nonatomic, assign) BOOL manualNavigationEnabled;
@property(nonatomic, assign) MptWebViewCallback callback;
@property(nonatomic, assign) void *callbackContext;
- (instancetype)initWithSource:(NSString *)source
                allowedOrigins:(NSString *)allowedOriginsJson
                       callback:(MptWebViewCallback)callback
                        context:(void *)context;
- (void)reloadSurface;
- (void)navigateSurface:(NSString *)source;
- (void)sendBridgeResponse:(NSString *)json;
- (void)focusSurface:(NSInteger)direction;
- (void)shutdown;
@end

@implementation MPTWebViewHost

- (instancetype)initWithSource:(NSString *)source
                allowedOrigins:(NSString *)allowedOriginsJson
                       callback:(MptWebViewCallback)callback
                        context:(void *)context {
    self = [super initWithFrame:NSMakeRect(0, 0, 1, 1)];
    if (self == nil) {
        return nil;
    }

    self.callback = callback;
    self.callbackContext = context;
    self.allowedOrigins = MptParseOrigins(allowedOriginsJson);
    self.wantsLayer = YES;

    WKWebViewConfiguration *configuration = [[WKWebViewConfiguration alloc] init];
    configuration.preferences.javaScriptCanOpenWindowsAutomatically = NO;
    configuration.websiteDataStore = WKWebsiteDataStore.defaultDataStore;
    WKUserContentController *contentController = [[WKUserContentController alloc] init];
    [contentController addScriptMessageHandler:self name:@"mptBridge"];
    [contentController addScriptMessageHandler:self name:@"mptHost"];

    NSString *bootstrap = [self bridgeBootstrapScript];
    WKUserScript *script = [[WKUserScript alloc]
        initWithSource:bootstrap
        injectionTime:WKUserScriptInjectionTimeAtDocumentStart
        forMainFrameOnly:YES];
    [contentController addUserScript:script];
    configuration.userContentController = contentController;

    self.webView = [[WKWebView alloc] initWithFrame:self.bounds configuration:configuration];
    self.webView.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    self.webView.navigationDelegate = self;
    self.webView.UIDelegate = self;
    [self addSubview:self.webView];

    NSURL *url = [NSURL URLWithString:source];
    if (url == nil || ![self isAllowedURL:url]) {
        MptEmit(self.callback, self.callbackContext, MptWebViewEventFailed, @"WKWebView source is outside the allowed origin policy.");
        return self;
    }

    MptEmit(self.callback, self.callbackContext, MptWebViewEventLoading, @"");
    if (url.isFileURL) {
        NSURL *readRoot = [url URLByDeletingLastPathComponent];
        [self.webView loadFileURL:url allowingReadAccessToURL:readRoot];
    } else {
        [self.webView loadRequest:[NSURLRequest requestWithURL:url]];
    }
    return self;
}

- (NSString *)bridgeBootstrapScript {
    NSMutableArray<NSString *> *sources = [NSMutableArray arrayWithObject:@"'self'"];
    for (NSURL *origin in self.allowedOrigins) {
        if (origin.isFileURL) {
            continue;
        }
        NSString *value = origin.absoluteString;
        while ([value hasSuffix:@"/"]) {
            value = [value substringToIndex:value.length - 1];
        }
        if (value.length > 0) {
            [sources addObject:value];
        }
    }
    NSString *sourceList = [sources componentsJoinedByString:@" "];
    NSString *csp = [NSString stringWithFormat:
        @"default-src %@ 'unsafe-inline' 'unsafe-eval' data: blob:; connect-src %@; img-src %@ data: blob:; media-src %@ data: blob:; frame-src %@; object-src 'none'; base-uri 'self'",
        sourceList, sourceList, sourceList, sourceList, sourceList];
    NSData *cspData = [NSJSONSerialization dataWithJSONObject:@[csp] options:0 error:nil];
    NSString *cspJson = [[NSString alloc] initWithData:cspData encoding:NSUTF8StringEncoding];
    NSArray<NSString *> *originValues = [self.allowedOrigins valueForKey:@"absoluteString"];
    NSData *originsData = [NSJSONSerialization dataWithJSONObject:originValues options:0 error:nil];
    NSString *originsJson = [[NSString alloc] initWithData:originsData encoding:NSUTF8StringEncoding];

    NSString *scriptTemplate = MptString(R"JS(
(() => {
  const trustedOrigins = %@;
  const trusted = trustedOrigins.some(origin => origin.startsWith('file:')
    ? location.href.startsWith(origin)
    : location.origin === origin.replace(/\/$/, ''));
  if (!trusted) return;
  const csp = %@[0];
  const installCsp = () => {
    if (document.querySelector('meta[data-mpt-origin-policy]')) return;
    const meta = document.createElement('meta');
    meta.httpEquiv = 'Content-Security-Policy';
    meta.content = csp;
    meta.dataset.mptOriginPolicy = '1';
    (document.head || document.documentElement).prepend(meta);
  };
  installCsp();
  const listeners = new Set();
  const bridge = {
    postMessage(payload) { window.webkit.messageHandlers.mptBridge.postMessage(payload); },
    addEventListener(type, callback) { if (type === 'message' && typeof callback === 'function') listeners.add(callback); },
    removeEventListener(type, callback) { if (type === 'message') listeners.delete(callback); }
  };
  globalThis.chrome = globalThis.chrome || {};
  globalThis.chrome.webview = bridge;
  globalThis.__mptReceive = payload => {
    const event = { data: payload };
    for (const callback of Array.from(listeners)) {
      try { callback(event); } catch {}
    }
  };
  addEventListener('keydown', event => {
    const command = event.metaKey || event.ctrlKey;
    let gesture = '';
    if (command && !event.altKey && event.shiftKey && event.key.toLowerCase() === 'p') gesture = 'Ctrl+Shift+P';
    else if (command && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'r') gesture = 'Ctrl+R';
    else if (command && event.altKey && !event.shiftKey && event.code === 'Space') gesture = 'Ctrl+Alt+Space';
    else if (!command && !event.altKey && !event.shiftKey && event.key === 'F5') gesture = 'F5';
    else if (!command && !event.altKey && !event.shiftKey && event.key === 'Escape') gesture = 'Escape';
    else if (command && !event.altKey && !event.shiftKey && /^[1-6]$/.test(event.key)) gesture = 'Ctrl+' + event.key;
    if (gesture) {
      event.preventDefault();
      window.webkit.messageHandlers.mptHost.postMessage({ kind: 'shortcut', value: gesture });
    }
  }, true);
})();
)JS");
    return [NSString stringWithFormat:
        scriptTemplate,
        originsJson ?: @"[]",
        cspJson ?: @"[\"default-src 'self'; object-src 'none'\"]"];
}

- (BOOL)isAllowedURL:(NSURL *)url {
    NSString *scheme = url.scheme.lowercaseString;
    if (!([scheme isEqualToString:@"http"] || [scheme isEqualToString:@"https"] || url.isFileURL)) {
        return NO;
    }
    if (url.user.length > 0 || url.password.length > 0) {
        return NO;
    }
    for (NSURL *origin in self.allowedOrigins) {
        if (origin.isFileURL) {
            if (url.isFileURL) {
                NSString *root = origin.path.stringByStandardizingPath;
                NSString *target = url.path.stringByStandardizingPath;
                if ([target isEqualToString:root] || [target hasPrefix:[root stringByAppendingString:@"/"]]) {
                    return YES;
                }
            }
            continue;
        }
        NSNumber *leftPort = url.port ?: ([url.scheme.lowercaseString isEqualToString:@"https"] ? @443 : @80);
        NSNumber *rightPort = origin.port ?: ([origin.scheme.lowercaseString isEqualToString:@"https"] ? @443 : @80);
        if ([url.scheme caseInsensitiveCompare:origin.scheme] == NSOrderedSame &&
            [url.host caseInsensitiveCompare:origin.host] == NSOrderedSame &&
            [leftPort isEqual:rightPort]) {
            return YES;
        }
    }
    return NO;
}

- (BOOL)isNavigableURL:(NSURL *)url {
    NSString *scheme = url.scheme.lowercaseString;
    if (!([scheme isEqualToString:@"http"] || [scheme isEqualToString:@"https"])) {
        return NO;
    }
    return url.user.length == 0 && url.password.length == 0;
}

- (void)reloadSurface {
    MptEmit(self.callback, self.callbackContext, MptWebViewEventLoading, @"");
    [self.webView reload];
}

- (void)navigateSurface:(NSString *)source {
    NSURL *url = [NSURL URLWithString:source];
    if (url == nil || !([self isNavigableURL:url] || [self isAllowedURL:url])) {
        return;
    }
    self.manualNavigationEnabled = [self isNavigableURL:url] && ![self isAllowedURL:url];
    MptEmit(self.callback, self.callbackContext, MptWebViewEventLoading, @"");
    if (url.isFileURL) {
        NSURL *readRoot = [url URLByDeletingLastPathComponent];
        [self.webView loadFileURL:url allowingReadAccessToURL:readRoot];
    } else {
        [self.webView loadRequest:[NSURLRequest requestWithURL:url]];
    }
}

- (void)sendBridgeResponse:(NSString *)json {
    if (json.length == 0 || json.length > 16384) {
        return;
    }
    NSString *script = [NSString stringWithFormat:@"globalThis.__mptReceive && globalThis.__mptReceive(%@);", json];
    [self.webView evaluateJavaScript:script completionHandler:nil];
}

- (void)focusSurface:(NSInteger)direction {
    (void)direction;
    [self.window makeFirstResponder:self.webView];
}

- (void)shutdown {
    self.webView.navigationDelegate = nil;
    self.webView.UIDelegate = nil;
    [self.webView.configuration.userContentController removeScriptMessageHandlerForName:@"mptBridge"];
    [self.webView.configuration.userContentController removeScriptMessageHandlerForName:@"mptHost"];
    [self.webView stopLoading];
    [self.webView removeFromSuperview];
    [self removeFromSuperview];
    self.webView = nil;
    self.callback = nullptr;
    self.callbackContext = nullptr;
}

- (void)webView:(WKWebView *)webView
    decidePolicyForNavigationAction:(WKNavigationAction *)navigationAction
    decisionHandler:(void (^)(WKNavigationActionPolicy))decisionHandler {
    NSURL *url = navigationAction.request.URL;
    decisionHandler(url != nil && (self.manualNavigationEnabled
        ? ([self isNavigableURL:url] || [self isAllowedURL:url])
        : [self isAllowedURL:url])
        ? WKNavigationActionPolicyAllow
        : WKNavigationActionPolicyCancel);
}

- (void)webView:(WKWebView *)webView
    decidePolicyForNavigationResponse:(WKNavigationResponse *)navigationResponse
    decisionHandler:(void (^)(WKNavigationResponsePolicy))decisionHandler {
    NSURL *url = navigationResponse.response.URL;
    decisionHandler(url != nil && navigationResponse.canShowMIMEType &&
        (self.manualNavigationEnabled
            ? ([self isNavigableURL:url] || [self isAllowedURL:url])
            : [self isAllowedURL:url])
        ? WKNavigationResponsePolicyAllow
        : WKNavigationResponsePolicyCancel);
}

- (void)webView:(WKWebView *)webView didStartProvisionalNavigation:(WKNavigation *)navigation {
    MptEmit(self.callback, self.callbackContext, MptWebViewEventLoading, @"");
}

- (void)webView:(WKWebView *)webView didFinishNavigation:(WKNavigation *)navigation {
    MptEmit(self.callback, self.callbackContext, MptWebViewEventReady, @"");
}

- (void)webView:(WKWebView *)webView
    didFailProvisionalNavigation:(WKNavigation *)navigation
    withError:(NSError *)error {
    MptEmit(self.callback, self.callbackContext, MptWebViewEventFailed, error.localizedDescription);
}

- (void)webView:(WKWebView *)webView
    didFailNavigation:(WKNavigation *)navigation
    withError:(NSError *)error {
    MptEmit(self.callback, self.callbackContext, MptWebViewEventFailed, error.localizedDescription);
}

- (nullable WKWebView *)webView:(WKWebView *)webView
    createWebViewWithConfiguration:(WKWebViewConfiguration *)configuration
    forNavigationAction:(WKNavigationAction *)navigationAction
    windowFeatures:(WKWindowFeatures *)windowFeatures {
    NSURL *url = navigationAction.request.URL;
    if (url != nil && (self.manualNavigationEnabled
        ? ([self isNavigableURL:url] || [self isAllowedURL:url])
        : [self isAllowedURL:url])) {
        [webView loadRequest:navigationAction.request];
    }
    return nil;
}

- (void)webView:(WKWebView *)webView
    requestMediaCapturePermissionForOrigin:(WKSecurityOrigin *)origin
    initiatedByFrame:(WKFrameInfo *)frame
    type:(WKMediaCaptureType)type
    decisionHandler:(void (^)(WKPermissionDecision))decisionHandler API_AVAILABLE(macos(12.0)) {
    decisionHandler(WKPermissionDecisionDeny);
}

- (void)userContentController:(WKUserContentController *)userContentController
    didReceiveScriptMessage:(WKScriptMessage *)message {
    NSURL *source = message.frameInfo.request.URL;
    if (source == nil || ![self isAllowedURL:source]) {
        return;
    }
    if ([message.name isEqualToString:@"mptHost"]) {
        if ([message.body isKindOfClass:NSDictionary.class]) {
            NSString *kind = ((NSDictionary *)message.body)[@"kind"];
            NSString *value = ((NSDictionary *)message.body)[@"value"];
            if ([kind isEqualToString:@"shortcut"] && [value isKindOfClass:NSString.class]) {
                MptEmit(self.callback, self.callbackContext, MptWebViewEventShortcut, value);
            }
        }
        return;
    }

    if (![NSJSONSerialization isValidJSONObject:message.body] &&
        ![message.body isKindOfClass:NSString.class] &&
        ![message.body isKindOfClass:NSNumber.class] &&
        message.body != NSNull.null) {
        return;
    }
    NSError *error = nil;
    NSData *data = [NSJSONSerialization dataWithJSONObject:message.body
                                                   options:NSJSONWritingFragmentsAllowed
                                                     error:&error];
    if (data.length == 0 || data.length > 16384 || error != nil) {
        return;
    }
    NSString *json = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
    MptEmit(self.callback, self.callbackContext, MptWebViewEventBridgeRequest, json);
}

@end

@interface MPTStatusItemHost : NSObject
@property(nonatomic, strong) NSStatusItem *statusItem;
@property(nonatomic, assign) MptTrayActionCallback callback;
@property(nonatomic, assign) void *callbackContext;
- (instancetype)initWithToolTip:(NSString *)toolTip
                       iconPath:(NSString *)iconPath
                       menuJson:(NSString *)menuJson
                       callback:(MptTrayActionCallback)callback
                        context:(void *)context;
- (BOOL)updateQuota:(NSInteger)remainingPercent toolTip:(NSString *)toolTip;
- (void)shutdown;
@end

static NSColor *MptQuotaAccentColor(NSInteger remainingPercent) {
    if (remainingPercent >= 50) {
        return [NSColor colorWithSRGBRed:25.0 / 255.0
                                  green:195.0 / 255.0
                                   blue:125.0 / 255.0
                                  alpha:1.0];
    }
    if (remainingPercent >= 20) {
        return [NSColor colorWithSRGBRed:250.0 / 255.0
                                  green:170.0 / 255.0
                                   blue:45.0 / 255.0
                                  alpha:1.0];
    }
    return [NSColor colorWithSRGBRed:245.0 / 255.0
                              green:75.0 / 255.0
                               blue:85.0 / 255.0
                              alpha:1.0];
}

static NSImage *MptQuotaImage(NSInteger remainingPercent, NSString *toolTip) {
    remainingPercent = MAX(0, MIN(100, remainingPercent));
    const NSInteger canvasSize = 64;
    NSBitmapImageRep *representation = [[NSBitmapImageRep alloc]
        initWithBitmapDataPlanes:nullptr
                      pixelsWide:canvasSize
                      pixelsHigh:canvasSize
                   bitsPerSample:8
                 samplesPerPixel:4
                        hasAlpha:YES
                        isPlanar:NO
                  colorSpaceName:NSDeviceRGBColorSpace
                    bitmapFormat:NSBitmapFormatAlphaNonpremultiplied
                     bytesPerRow:0
                    bitsPerPixel:0];
    if (representation == nil) {
        return nil;
    }

    NSGraphicsContext *context = [NSGraphicsContext graphicsContextWithBitmapImageRep:representation];
    if (context == nil) {
        return nil;
    }

    [NSGraphicsContext saveGraphicsState];
    [NSGraphicsContext setCurrentContext:context];
    context.shouldAntialias = YES;
    context.imageInterpolation = NSImageInterpolationHigh;
    [NSColor.clearColor setFill];
    NSRectFillUsingOperation(NSMakeRect(0, 0, canvasSize, canvasSize), NSCompositingOperationCopy);

    const NSRect ringBounds = NSMakeRect(6.5, 6.5, 51.0, 51.0);
    NSBezierPath *track = [NSBezierPath bezierPathWithOvalInRect:ringBounds];
    track.lineWidth = 7.0;
    track.lineCapStyle = NSRoundLineCapStyle;
    [[NSColor colorWithSRGBRed:68.0 / 255.0
                        green:75.0 / 255.0
                         blue:88.0 / 255.0
                        alpha:0.82] setStroke];
    [track stroke];

    if (remainingPercent > 0) {
        NSBezierPath *arc = [NSBezierPath bezierPath];
        arc.lineWidth = 7.0;
        arc.lineCapStyle = NSRoundLineCapStyle;
        [arc appendBezierPathWithArcWithCenter:NSMakePoint(32.0, 32.0)
                                       radius:25.5
                                   startAngle:90.0
                                     endAngle:90.0 - (remainingPercent * 3.6)
                                    clockwise:YES];
        [MptQuotaAccentColor(remainingPercent) setStroke];
        [arc stroke];
    }

    NSString *text = [NSString stringWithFormat:@"%ld", (long)remainingPercent];
    CGFloat fontSize = remainingPercent >= 100 ? 19.0 : 24.0;
    NSShadow *shadow = [[NSShadow alloc] init];
    shadow.shadowColor = [NSColor colorWithWhite:0.0 alpha:0.82];
    shadow.shadowOffset = NSMakeSize(1.0, -1.0);
    shadow.shadowBlurRadius = 1.0;
    NSDictionary<NSAttributedStringKey, id> *attributes = @{
        NSFontAttributeName: [NSFont monospacedDigitSystemFontOfSize:fontSize
                                                             weight:NSFontWeightBold],
        NSForegroundColorAttributeName: NSColor.whiteColor,
        NSShadowAttributeName: shadow
    };
    NSSize textSize = [text sizeWithAttributes:attributes];
    [text drawAtPoint:NSMakePoint(
        std::floor((canvasSize - textSize.width) / 2.0),
        std::floor((canvasSize - textSize.height) / 2.0) - 1.0)
       withAttributes:attributes];
    [context flushGraphics];
    [NSGraphicsContext restoreGraphicsState];

    representation.size = NSMakeSize(22.0, 22.0);
    NSImage *image = [[NSImage alloc] initWithSize:NSMakeSize(22.0, 22.0)];
    [image addRepresentation:representation];
    [image setTemplate:NO];
    image.accessibilityDescription = toolTip;
    return image;
}

@implementation MPTStatusItemHost

- (instancetype)initWithToolTip:(NSString *)toolTip
                       iconPath:(NSString *)iconPath
                       menuJson:(NSString *)menuJson
                       callback:(MptTrayActionCallback)callback
                        context:(void *)context {
    self = [super init];
    if (self == nil) {
        return nil;
    }
    self.callback = callback;
    self.callbackContext = context;
    self.statusItem = [NSStatusBar.systemStatusBar statusItemWithLength:NSSquareStatusItemLength];
    NSStatusBarButton *button = self.statusItem.button;
    button.toolTip = toolTip;
    NSImage *image = iconPath.length > 0 ? [[NSImage alloc] initWithContentsOfFile:iconPath] : nil;
    if (image == nil) {
        image = [NSImage imageWithSystemSymbolName:@"bolt.circle" accessibilityDescription:toolTip];
    }
    if (image != nil) {
        [image setTemplate:YES];
        button.image = image;
    } else {
        button.title = @"M";
    }

    NSData *menuData = [menuJson dataUsingEncoding:NSUTF8StringEncoding];
    id decoded = menuData.length > 0
        ? [NSJSONSerialization JSONObjectWithData:menuData options:0 error:nil]
        : nil;
    NSArray *items = [decoded isKindOfClass:NSArray.class] ? decoded : @[];
    NSMenu *menu = [[NSMenu alloc] initWithTitle:toolTip];
    menu.autoenablesItems = NO;
    for (id value in items) {
        if (![value isKindOfClass:NSDictionary.class]) {
            continue;
        }
        NSDictionary *definition = value;
        NSString *actionId = definition[@"id"];
        NSString *label = definition[@"label"];
        if (![actionId isKindOfClass:NSString.class] || ![label isKindOfClass:NSString.class]) {
            continue;
        }
        if ([definition[@"separatorBefore"] boolValue] && menu.numberOfItems > 0) {
            [menu addItem:NSMenuItem.separatorItem];
        }
        NSMenuItem *item = [[NSMenuItem alloc] initWithTitle:label
                                                    action:@selector(invokeMenuItem:)
                                             keyEquivalent:@""];
        item.target = self;
        item.representedObject = actionId;
        item.enabled = YES;
        if ([definition[@"isDefault"] boolValue]) {
            item.attributedTitle = [[NSAttributedString alloc]
                initWithString:label
                attributes:@{ NSFontAttributeName: [NSFont boldSystemFontOfSize:[NSFont systemFontSize]] }];
        }
        [menu addItem:item];
    }
    self.statusItem.menu = menu;
    return self;
}

- (BOOL)updateQuota:(NSInteger)remainingPercent toolTip:(NSString *)toolTip {
    NSStatusBarButton *button = self.statusItem.button;
    if (button == nil) {
        return NO;
    }
    NSImage *image = MptQuotaImage(remainingPercent, toolTip);
    if (image == nil) {
        return NO;
    }
    button.toolTip = toolTip;
    button.title = @"";
    button.image = image;
    return YES;
}

- (void)invokeMenuItem:(NSMenuItem *)sender {
    NSString *actionId = sender.representedObject;
    if (self.callback != nullptr && [actionId isKindOfClass:NSString.class]) {
        self.callback(self.callbackContext, actionId.UTF8String);
    }
}

- (void)shutdown {
    self.statusItem.menu = nil;
    if (self.statusItem != nil) {
        [NSStatusBar.systemStatusBar removeStatusItem:self.statusItem];
    }
    self.statusItem = nil;
    self.callback = nullptr;
    self.callbackContext = nullptr;
}

@end

@interface MPTNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end

@implementation MPTNotificationDelegate
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
       willPresentNotification:(UNNotification *)notification
         withCompletionHandler:(void (^)(UNNotificationPresentationOptions options))completionHandler {
    completionHandler(UNNotificationPresentationOptionBanner |
                      UNNotificationPresentationOptionList |
                      UNNotificationPresentationOptionSound);
}

- (void)userNotificationCenter:(UNUserNotificationCenter *)center
didReceiveNotificationResponse:(UNNotificationResponse *)response
         withCompletionHandler:(void (^)(void))completionHandler {
    NSString *activationUri = response.notification.request.content.userInfo[@"activationUri"];
    if ([activationUri isKindOfClass:NSString.class] && activationUri.length > 0) {
        NSURL *url = [NSURL URLWithString:activationUri];
        if (url != nil) {
            [NSWorkspace.sharedWorkspace openURL:url];
        }
    }
    completionHandler();
}
@end

static MPTNotificationDelegate *MptNotificationDelegateInstance(void) {
    static MPTNotificationDelegate *instance;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        instance = [[MPTNotificationDelegate alloc] init];
        UNUserNotificationCenter.currentNotificationCenter.delegate = instance;
    });
    return instance;
}

extern "C" {

void *mpt_webview_create(const char *source,
                         const char *allowedOriginsJson,
                         MptWebViewCallback callback,
                         void *context) {
    __block MPTWebViewHost *host = nil;
    void (^createBlock)(void) = ^{
        host = [[MPTWebViewHost alloc]
            initWithSource:MptString(source)
            allowedOrigins:MptString(allowedOriginsJson)
            callback:callback
            context:context];
    };
    if (NSThread.isMainThread) {
        createBlock();
    } else {
        dispatch_sync(dispatch_get_main_queue(), createBlock);
    }
    return (__bridge_retained void *)host;
}

void *mpt_status_item_create(const char *toolTip,
                             const char *iconPath,
                             const char *menuJson,
                             MptTrayActionCallback callback,
                             void *context) {
    __block MPTStatusItemHost *host = nil;
    void (^createBlock)(void) = ^{
        host = [[MPTStatusItemHost alloc]
            initWithToolTip:MptString(toolTip)
            iconPath:MptString(iconPath)
            menuJson:MptString(menuJson)
            callback:callback
            context:context];
    };
    if (NSThread.isMainThread) {
        createBlock();
    } else {
        dispatch_sync(dispatch_get_main_queue(), createBlock);
    }
    return (__bridge_retained void *)host;
}

int mpt_status_item_update_quota(void *handle,
                                 int remainingPercent,
                                 const char *toolTip) {
    if (handle == nullptr) {
        return 0;
    }
    MPTStatusItemHost *host = (__bridge MPTStatusItemHost *)handle;
    NSString *toolTipValue = MptString(toolTip);
    __block BOOL updated = NO;
    void (^updateBlock)(void) = ^{
        updated = [host updateQuota:remainingPercent toolTip:toolTipValue];
    };
    if (NSThread.isMainThread) {
        updateBlock();
    } else {
        dispatch_sync(dispatch_get_main_queue(), updateBlock);
    }
    return updated ? 1 : 0;
}

void mpt_status_item_destroy(void *handle) {
    if (handle == nullptr) {
        return;
    }
    MPTStatusItemHost *host = (__bridge_transfer MPTStatusItemHost *)handle;
    void (^destroyBlock)(void) = ^{ [host shutdown]; };
    if (NSThread.isMainThread) {
        destroyBlock();
    } else {
        dispatch_sync(dispatch_get_main_queue(), destroyBlock);
    }
}

void mpt_webview_reload(void *handle) {
    MPTWebViewHost *host = (__bridge MPTWebViewHost *)handle;
    dispatch_async(dispatch_get_main_queue(), ^{ [host reloadSurface]; });
}

void mpt_webview_navigate(void *handle, const char *source) {
    MPTWebViewHost *host = (__bridge MPTWebViewHost *)handle;
    NSString *value = MptString(source);
    dispatch_async(dispatch_get_main_queue(), ^{ [host navigateSurface:value]; });
}

void mpt_webview_send_bridge_response(void *handle, const char *json) {
    MPTWebViewHost *host = (__bridge MPTWebViewHost *)handle;
    NSString *value = MptString(json);
    dispatch_async(dispatch_get_main_queue(), ^{ [host sendBridgeResponse:value]; });
}

void mpt_webview_focus(void *handle, int direction) {
    MPTWebViewHost *host = (__bridge MPTWebViewHost *)handle;
    dispatch_async(dispatch_get_main_queue(), ^{ [host focusSurface:direction]; });
}

void mpt_webview_set_visible(void *handle, int visible) {
    MPTWebViewHost *host = (__bridge MPTWebViewHost *)handle;
    dispatch_async(dispatch_get_main_queue(), ^{ host.hidden = visible == 0; });
}

void mpt_webview_destroy(void *handle) {
    if (handle == nullptr) {
        return;
    }
    MPTWebViewHost *host = (__bridge_transfer MPTWebViewHost *)handle;
    void (^destroyBlock)(void) = ^{ [host shutdown]; };
    if (NSThread.isMainThread) {
        destroyBlock();
    } else {
        dispatch_sync(dispatch_get_main_queue(), destroyBlock);
    }
}

int mpt_notification_publish(const char *identifier,
                             const char *title,
                             const char *body,
                             const char *activationUri) {
    if (@available(macOS 11.0, *)) {
        UNUserNotificationCenter *center = UNUserNotificationCenter.currentNotificationCenter;
        (void)MptNotificationDelegateInstance();
        UNMutableNotificationContent *content = [[UNMutableNotificationContent alloc] init];
        content.title = MptString(title);
        content.body = MptString(body);
        content.sound = UNNotificationSound.defaultSound;
        NSString *uri = MptString(activationUri);
        if (uri.length > 0) {
            content.userInfo = @{ @"activationUri": uri };
        }
        UNNotificationRequest *request = [UNNotificationRequest
            requestWithIdentifier:MptString(identifier)
            content:content
            trigger:nil];
        [center requestAuthorizationWithOptions:(UNAuthorizationOptionAlert |
                                                  UNAuthorizationOptionSound |
                                                  UNAuthorizationOptionBadge)
                              completionHandler:^(BOOL granted, NSError *error) {
            if (granted && error == nil) {
                [center addNotificationRequest:request withCompletionHandler:nil];
            }
        }];
        return 0;
    }
    return -1;
}

int mpt_pasteboard_read_png(void **bytes,
                            size_t *length,
                            int *width,
                            int *height) {
    if (bytes == nullptr || length == nullptr || width == nullptr || height == nullptr) {
        return -1;
    }

    *bytes = nullptr;
    *length = 0;
    *width = 0;
    *height = 0;

    @autoreleasepool {
        NSPasteboard *pasteboard = NSPasteboard.generalPasteboard;
        NSData *png = [pasteboard dataForType:NSPasteboardTypePNG];
        NSBitmapImageRep *representation = png.length > 0
            ? [NSBitmapImageRep imageRepWithData:png]
            : nil;

        if (png.length == 0 || representation == nil) {
            NSImage *image = [[NSImage alloc] initWithPasteboard:pasteboard];
            NSData *tiff = image.TIFFRepresentation;
            representation = tiff.length > 0
                ? [NSBitmapImageRep imageRepWithData:tiff]
                : nil;
            png = representation == nil
                ? nil
                : [representation representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        }

        if (png.length == 0 || representation == nil) {
            return 1;
        }
        if (png.length > INT_MAX ||
            representation.pixelsWide > INT_MAX ||
            representation.pixelsHigh > INT_MAX) {
            return -2;
        }

        void *copy = malloc(png.length);
        if (copy == nullptr) {
            return -3;
        }

        memcpy(copy, png.bytes, png.length);
        *bytes = copy;
        *length = png.length;
        *width = (int)representation.pixelsWide;
        *height = (int)representation.pixelsHigh;
        return 0;
    }
}

int mpt_pasteboard_write_text(const char *value) {
    @autoreleasepool {
        NSPasteboard *pasteboard = NSPasteboard.generalPasteboard;
        [pasteboard clearContents];
        return [pasteboard setString:MptString(value) forType:NSPasteboardTypeString] ? 0 : -1;
    }
}

static NSDictionary *MptKeychainQuery(NSString *service, NSString *account) {
    return @{
        (__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService: service,
        (__bridge id)kSecAttrAccount: account
    };
}

int mpt_keychain_save(const char *service, const char *account, const char *value) {
    NSMutableDictionary *query = [MptKeychainQuery(MptString(service), MptString(account)) mutableCopy];
    SecItemDelete((__bridge CFDictionaryRef)query);
    query[(__bridge id)kSecValueData] = [MptString(value) dataUsingEncoding:NSUTF8StringEncoding];
    query[(__bridge id)kSecAttrAccessible] = (__bridge id)kSecAttrAccessibleAfterFirstUnlock;
    return (int)SecItemAdd((__bridge CFDictionaryRef)query, nullptr);
}

int mpt_keychain_read(const char *service, const char *account, char **value) {
    if (value == nullptr) {
        return (int)errSecParam;
    }
    *value = nullptr;
    NSMutableDictionary *query = [MptKeychainQuery(MptString(service), MptString(account)) mutableCopy];
    query[(__bridge id)kSecReturnData] = @YES;
    query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
    CFTypeRef result = nullptr;
    OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
    if (status != errSecSuccess) {
        return (int)status;
    }
    NSData *data = CFBridgingRelease(result);
    NSString *string = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
    if (string == nil) {
        return (int)errSecDecode;
    }
    *value = strdup(string.UTF8String);
    return *value == nullptr ? (int)errSecAllocate : (int)errSecSuccess;
}

int mpt_keychain_delete(const char *service, const char *account) {
    return (int)SecItemDelete((__bridge CFDictionaryRef)MptKeychainQuery(MptString(service), MptString(account)));
}

void mpt_free(void *value) {
    free(value);
}

}
