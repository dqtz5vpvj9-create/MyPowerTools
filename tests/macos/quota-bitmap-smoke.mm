// macOS integration check: exercise the same AppKit bitmap renderer as the live tray.
// Run from the repository root:
// xcrun clang++ -std=c++17 -fobjc-arc -framework Cocoa -framework WebKit \
//   -framework UserNotifications -framework Security tests/macos/quota-bitmap-smoke.mm \
//   -o /tmp/mpt-quota-bitmap-smoke && /tmp/mpt-quota-bitmap-smoke
#pragma clang diagnostic ignored "-Wnullability-completeness"
#include "../../native/macos/MptMacNative/MptMacNative.mm"
int main() {
    @autoreleasepool {
        const NSInteger percentages[] = {0, 37, 100};
        for (NSInteger percentage : percentages) {
            NSImage *image = MptQuotaImage(percentage, @"Quota rendering check");
            if (image == nil || image.size.width != 22 || image.representations.count != 1) {
                fprintf(stderr, "Quota image unavailable at %ld%%\n", (long)percentage);
                return 1;
            }
            NSBitmapImageRep *bitmap = (NSBitmapImageRep *)image.representations.firstObject;
            BOOL hasVisiblePixel = NO;
            for (NSInteger y = 0; y < bitmap.pixelsHigh && !hasVisiblePixel; ++y) {
                for (NSInteger x = 0; x < bitmap.pixelsWide; ++x) {
                    if ([bitmap colorAtX:x y:y].alphaComponent > 0) {
                        hasVisiblePixel = YES;
                        break;
                    }
                }
            }
            if (!hasVisiblePixel) return 2;
        }
        puts("Native quota images contain visible pixels.");
    }
}
