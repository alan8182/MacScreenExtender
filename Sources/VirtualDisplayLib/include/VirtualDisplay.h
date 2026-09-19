#import <Foundation/Foundation.h>

// 创建虚拟显示器，返回 displayID（失败返回 0）。对象由本库持有，销毁用 destroyVirtualDisplay。
// - logicalWidth/Height: 系统 UI 排版点阵尺寸 (point，决定字号与窗口逻辑大小)
// - backingWidth/Height: 底层像素缓冲区尺寸 (pixel backing store，当 hiDPI=1 时为 logical 的 2 倍)
uint32_t createVirtualDisplay(int32_t logicalWidth, int32_t logicalHeight,
                              int32_t backingWidth, int32_t backingHeight,
                              int32_t ppi, bool hiDPI,
                              NSString * _Nullable name, uint32_t serialNum, int32_t refreshRate);

// 销毁虚拟显示器（释放后 macOS 桌面布局会回落）
void destroyVirtualDisplay(uint32_t displayID);
