# SphereMapPlus

FF14 (XIV) Dalamud 插件:运行时替换全局球面贴图,不改任何游戏文件。

## 这是什么

7.0 后,角色材质的球面贴图(Sphere Map)不再由材质文件引用,而是集中在一个全局纹理数组
(`chara/common/texture/sphere_d_array.tex`,32 切片)中,材质只能通过索引引用它——
社区因此认为球面贴图"被硬编码、无法自定义"。

本插件的做法:

- 把方案引用的自定义贴图(PNG)缩放至 256×256、生成 9 级 mip、**BC1 压缩**(与原生一致)
- 用完整数据**新建一个可采样纹理 + SRV**,把 `Kernel.Texture` 内的 D3D 指针换成新的
  (游戏的源纹理是 `IMMUTABLE` 只读,无法就地写入;指针交换与 Penumbra LivePreview 同款手法)
- 全程只动内存与 GPU 资源,**零游戏文件修改**,卸载/还原自动恢复原指针

替换生效后,Penumbra 材质编辑器原生Sphere Map 切片选择与强度调节、TexTools(挂接 Penumbra)
等全部照常可用——本插件只负责"提供贴图",材质编辑交给生态工具。

## 功能

- **切片预览 / PNG 导出**:实时预览全局球面数组 32 个切片,可逐片导出为 PNG(素材提取)
- **贴图仓库**:PNG 复制入库统一托管,重命名 / 删除 / 缩略图
- **直接导入**:选切片 + PNG 快速试验(自动缩放 / mip / BC1)
- **方案**:命名的一组 {切片 ← 仓库贴图},可同时替换多个切片,列表顺序即优先级
- **联动自动切换**:方案绑定 Penumbra mod;Glamourer 切幻化 / 换装 / 角色重绘时,
  通过 Penumbra 资源树获取"玩家当前实际生效的 mod",命中即自动应用对应方案
- **方案导出 / 导入**:`.smpk` 包(ZIP:方案清单 + 绑定 mod 名单 + 全部引用贴图),可直接分享

## 安装

### 方式一:插件仓库(推荐,可自动更新)

1. 游戏内输入 `/xlsettings`,进入 **实验性(Experimental)** 选项卡;
2. 在 **自定义插件仓库(Custom Plugin Repositories)** 中添加:

   ```
   https://raw.githubusercontent.com/NahayaSakafuku/SphereMapPlus/main/repo.json
   ```

3. 点击右下角 **保存**;
4. 打开 **Dalamud 插件列表**(系统菜单 → Dalamud Plugins),搜索 `SphereMapPlus` 并安装。

**国内网络镜像**:如果 `raw.githubusercontent.com` 无法访问,可改用 jsDelivr CDN 地址(内容相同,国内可直连):

```
https://cdn.jsdelivr.net/gh/NahayaSakafuku/SphereMapPlus@main/repo.json
```

### 方式二:手动安装

从 [Releases](https://github.com/NahayaSakafuku/SphereMapPlus/releases) 下载 `latest.zip`,解压到
`%APPDATA%\XIVLauncherCN\installedPlugins\SphereMapPlus\<版本>\`,重启游戏后在 `/xlplugins` 启用;
右上角齿轮图标打开主窗口。

## 使用建议

- 方案绑定按 **mod 名称**匹配(需与 Penumbra 列表中的名称一致);Glamourer 方案里的装备
  来自哪些 mod,就绑哪些 mod
- 切片 19-23 / 27 / 29-31 是着色器瓦片纹理,替换会影响使用它们的材质;空片是安全目标
- 测试高亮(全部切片变纯色)可快速验证替换链路

## 构建

- .NET SDK(与 Dalamud API 15 匹配)、Dalamud.NET.SDK 15.0.0
- `dotnet build -c Release`,产物 `SphereMapPlus/bin/Release/SphereMapPlus/latest.zip`
- `PngTest/`:离线验证项目(PNG 读写 / BC1 编解码 / D3D 全链路),开发期回归用

## 致谢

- [Penumbra](https://github.com/xivdev/Penumbra) / [Luna](https://github.com/Ottermandias/Luna)
  ——资源树 IPC、切片选择器(Luna TextureArraySlicer)与指针交换手法均参考其实现
- [Glamourer](https://github.com/Ottermandias/Glamourer) ——StateChanged 事件联动
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) ——游戏结构体
