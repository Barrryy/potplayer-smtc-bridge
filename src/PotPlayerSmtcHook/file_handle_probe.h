#pragma once

// 从 PotPlayer 自己打开的文件句柄里找出当前媒体路径。
// 这用于兜住新版播放器绕过 CreateFileW/CreateFile2 导出钩子的情况。
void FileHandleProbe_Start();
