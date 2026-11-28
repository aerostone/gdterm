# 端口转发修复验收证据

验证源码提交：fdb79dc。GitHub 46152072567 与 AppVeyor 55184134 / 0.1.570 全部成功，CI URL 及 SHA 见 verification.json。收尾提交仅更新验收记录、图片和经审图补入的两条像素基线，业务源码与此提交一致。

- port-forward-smoke.log：GitHub Linux 隔离 OpenSSH 84 项断言；port-forward-local.log 为本机同组验证。涉及真实 SSH、TCP 数据、暂停进程的慢响应、并发批量与退出竞态。
- test-run.log：Windows 1089 项单元断言；ui-smoke.log：10 组桌面场景，包括真实表单提交、取消、磁盘拒写、选择范围、面板释放/恢复。
- ui-matrix-report.txt：69 页×6组=414 次布局采集；字号/DPI/主题有效性及逐页检查通过。
- PNG/同名 JSON：最终源码的真实 WinForms 绘制与控件树。运行/忙碌/失败并列页面使用状态夹具验证显示及按钮；网络状态转换由实连测试验证，不把状态夹具说成 Windows 实连。
- 系统级鼠标键盘输入未自动执行；按钮与双击入口通过控件 API/事件触发。原始完整 artifact 位于 /tmp/gdterm-ui-46152072567/，可从 GitHub run 重新获取。
- 远程监听地址能否开放非回环仍由服务器 GatewayPorts 控制；规则重启保存，不自动恢复监听。
