#import <Foundation/Foundation.h>

// 创建虚拟显示器，返回 displayID（失败返回 0）。对象由本库持有，销毁用 destroyVirtualDisplay。
uint32_t createVirtualDisplay(int32_t width, int32_t height, int32_t ppi, bool hiDPI,
                              NSString * _Nullable name, uint32_t serialNum, int32_t refreshRate);

// 销毁虚拟显示器（释放后 macOS 桌面布局会回落）
void destroyVirtualDisplay(uint32_t displayID);
