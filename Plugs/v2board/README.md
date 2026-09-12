## `v2board`对接`TokenPay`

### 1. 将插件复制到`v2board`对应目录
### 2. 到`v2board`后台-**支付配置**中添加支付方式
注意事项
1. API地址末尾请不要有斜线，如`https://token-pay.xxx.com`  
2. 币种请填写指定字符，支持的币种请参考[币种说明](../../Wiki/Currency.md) 
3. 如果你要同时支持USDT和TRX付款，你需要添加两条支付方式，依此类推  

请参考此图填写
<img src="../../Wiki/imgs/v2board-payment.png" alt="v2board支付方式配置"/>

### 升级后的回调验签

新版 TokenPay 回调包含布尔字段 `IsCustomAmount`，本插件按发送端规则将布尔值签名为 `True` / `False`，同时保留数值 `0`。旧插件使用 `http_build_query` 将布尔值转换为 `1` / `0`，会导致原站返回 `cannot pass verification`。

备份并替换 **V2Board 站点目录**下的 `app/Payments/TokenPay.php` 即可更新插件，无需重新编译 TokenPay。若使用 Webman 常驻进程或关闭时间戳检查的 OPcache，请重启对应进程使新文件生效。本插件使用默认 MD5 模式，两端 API Token 必须一致。

对已经到账、三次自动通知均失败的订单，更新插件后在 TokenPay 后台订单列表点击「重试回调」，检查是否返回 `ok`，并确认原站订单已完成。

回归验证：`php Plugs/v2board/tests/callback_signature_test.php`。
