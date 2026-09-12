<?php
// Run with: php Plugs/v2board/tests/callback_signature_test.php
require dirname(__DIR__) . '/app/Payments/TokenPay.php';

if (isset($argv[1]) && $argv[1] === '--notify') {
    $plugin = new \App\Payments\TokenPay(['token_pay_apitoken' => '666']);
    echo json_encode($plugin->notify(json_decode(base64_decode($argv[2]), true)));
    exit;
}

// Fixed signatures use the public test key "666" and independently constructed
// TokenPay canonical strings. Never recompute expected signatures in this test.
$base = [
    'ActualAmount' => '9.9',
    'Amount' => '1.4754',
    'Id' => 'TEST-PAYMENT-001',
    'IsCustomAmount' => false,
    'IsDynamicAmount' => 0,
    'OrderUserKey' => '42',
    'OutOrderId' => 'TEST-ORDER-001',
    'PayAmount' => '1.4754',
    'Status' => 1,
    'Signature' => 'ffce12e4b24d2993a1fc7a4a8ec12187',
];
$ok = json_encode([
    'trade_no' => 'TEST-ORDER-001',
    'callback_no' => 'TEST-PAYMENT-001',
    'custom_result' => 'ok',
]);
$rejected = 'cannot pass verification';
$legacy = $base;
unset($legacy['IsCustomAmount']);
$legacy['Signature'] = '650b7d32012c3b1d499adcfbd016ec1f';
$missingSign = $base;
unset($missingSign['Signature']);
$missingZero = $base;
unset($missingZero['IsDynamicAmount']);
$cases = [
    ['false boolean callback', $base, $ok],
    ['true boolean callback', array_replace($base, [
        'IsCustomAmount' => true,
        'Signature' => '8f81fbac5a8675ffeffc5e600e7c8b39',
    ]), $ok],
    ['legacy callback without boolean', $legacy, $ok],
    ['numeric zero is retained', array_replace($base, [
        'IsCustomAmount' => 0,
        'Signature' => 'cd459a713146171ed87f1959bc05899b',
    ]), $ok],
    ['field order does not matter', array_reverse($base, true), $ok],
    ['null and empty optional fields', array_replace($base, [
        'MinCustomAmount' => null,
        'PassThroughInfo' => '',
    ]), $ok],
    ['literal passthrough characters', array_replace($base, [
        'PassThroughInfo' => 'note\\keep + &= %',
        'Signature' => '1ab04f52353b890b613bd9289842d2a9',
    ]), $ok],
    ['tampered boolean', array_replace($base, ['IsCustomAmount' => true]), $rejected],
    ['boolean replaced by numeric zero', array_replace($base, ['IsCustomAmount' => 0]), $rejected],
    ['zero field removed', $missingZero, $rejected],
    ['tampered amount', array_replace($base, ['ActualAmount' => '99']), $rejected],
    ['tampered order number', array_replace($base, ['OutOrderId' => 'OTHER-ORDER']), $rejected],
    ['tampered passthrough', array_replace($base, ['PassThroughInfo' => 'extra']), $rejected],
    ['wrong signature', array_replace($base, ['Signature' => str_repeat('0', 32)]), $rejected],
    ['missing signature', $missingSign, $rejected],
    ['null signature', array_replace($base, ['Signature' => null]), $rejected],
    ['array signature', array_replace($base, ['Signature' => []]), $rejected],
    ['non-scalar callback field', array_replace($base, ['PassThroughInfo' => ['value']]), $rejected],
    ['signed pending order', array_replace($base, [
        'Status' => 0,
        'Signature' => 'edf22ae99148805f0dc1ef4655c4132f',
    ]), 'failed'],
    ['signed expired order', array_replace($base, [
        'Status' => 2,
        'Signature' => '6f8b87d390553d5e71f1db90a52ffc3e',
    ]), 'failed'],
];
$failed = 0;
foreach ($cases as $case) {
    list($name, $params, $expected) = $case;
    // notify() intentionally terminates rejected requests; isolate each request.
    $process = proc_open(
        [PHP_BINARY, __FILE__, '--notify', base64_encode(json_encode($params))],
        [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']],
        $pipes
    );
    if (!is_resource($process)) {
        throw new \RuntimeException('Could not start PHP callback test process.');
    }
    fclose($pipes[0]);
    $output = stream_get_contents($pipes[1]);
    $errors = stream_get_contents($pipes[2]);
    fclose($pipes[1]);
    fclose($pipes[2]);
    $exitCode = proc_close($process);
    if ($exitCode !== 0 || $output !== $expected || $errors !== '') {
        fwrite(STDERR, "FAIL: $name (exit $exitCode)\n$output\n$errors\n");
        $failed++;
    } else {
        echo "PASS: $name\n";
    }
}
echo count($cases) . " cases, $failed failures\n";
exit($failed === 0 ? 0 : 1);
