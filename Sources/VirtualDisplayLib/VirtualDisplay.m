#import "include/VirtualDisplay.h"
#import "CGVirtualDisplayPrivate.h"

// displayID -> CGVirtualDisplay 持有表。ARC 持有对象，释放即销毁显示器。
static NSMutableDictionary<NSNumber *, CGVirtualDisplay *> *gLiveDisplays = nil;

uint32_t createVirtualDisplay(int32_t width, int32_t height, int32_t ppi, bool hiDPI,
                              NSString * _Nullable name, uint32_t serialNum, int32_t refreshRate) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{ gLiveDisplays = [NSMutableDictionary dictionary]; });

    // 实测语义：mode = 像素尺寸（backing），hiDPI=1 表示 2x UI → 逻辑分辨率 = mode/2。
    // （此前按"mode=逻辑尺寸"减半传入，结果 hiDPI 不生效，退回 1x 渲染）
    int modeW = width, modeH = height;

    CGVirtualDisplaySettings *settings = [[CGVirtualDisplaySettings alloc] init];
    settings.hiDPI = hiDPI ? 1 : 0;
    settings.modes = @[[[CGVirtualDisplayMode alloc] initWithWidth:(unsigned int)modeW
                                                             height:(unsigned int)modeH
                                                        refreshRate:(refreshRate > 0 ? refreshRate : 60)]];

    CGVirtualDisplayDescriptor *descriptor = [[CGVirtualDisplayDescriptor alloc] init];
    descriptor.queue = dispatch_get_global_queue(DISPATCH_QUEUE_PRIORITY_HIGH, 0);
    descriptor.name = name ?: @"Virtual Display";
    descriptor.whitePoint = CGPointMake(0.3125, 0.3291);
    descriptor.redPrimary = CGPointMake(0.6797, 0.3203);
    descriptor.greenPrimary = CGPointMake(0.2559, 0.6983);
    descriptor.bluePrimary = CGPointMake(0.1494, 0.0557);
    descriptor.maxPixelsWide = (unsigned int)width;
    descriptor.maxPixelsHigh = (unsigned int)height;
    descriptor.sizeInMillimeters = CGSizeMake(25.4 * width / ppi, 25.4 * height / ppi);
    descriptor.vendorID = 1;

    // serial 冲突时递增重试；连续失败再换随机序列号兜底
    //（防历史残留/僵尸显示器占住稳定序列号段导致永久创建失败）
    CGVirtualDisplay *display = nil;
    for (unsigned int attempt = 0; attempt < 10 && display == nil; attempt++) {
        descriptor.serialNum = serialNum + attempt;
        descriptor.productID = descriptor.serialNum;
        display = [[CGVirtualDisplay alloc] initWithDescriptor:descriptor];
        if (![display applySettings:settings] || display.displayID == 0) display = nil;
    }
    if (!display) {
        uint32_t rnd = (uint32_t)arc4random_uniform(60000u) + 1000u;
        for (unsigned int attempt = 0; attempt < 10 && display == nil; attempt++) {
            descriptor.serialNum = rnd + attempt;
            descriptor.productID = descriptor.serialNum;
            display = [[CGVirtualDisplay alloc] initWithDescriptor:descriptor];
            if (![display applySettings:settings] || display.displayID == 0) display = nil;
        }
        if (display) NSLog(@"[VirtualDisplay] 稳定序列号段被占用，已改用随机序列号 %u 创建成功", rnd);
    }
    if (!display) {
        NSLog(@"[VirtualDisplay] 创建失败：20 次尝试均失败（mode %dx%d@%d）",
              modeW, modeH, refreshRate > 0 ? refreshRate : 60);
        return 0;
    }

    uint32_t displayID = display.displayID;
    if (displayID == 0) {
        NSLog(@"[VirtualDisplay] 创建后 displayID 为 0");
        return 0;
    }

    gLiveDisplays[@(displayID)] = display;
    return displayID;
}

void destroyVirtualDisplay(uint32_t displayID) {
    [gLiveDisplays removeObjectForKey:@(displayID)];
}
