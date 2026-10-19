param([string]$BaseUrl = 'http://127.0.0.1:18777', [string]$Addon = '_ActionBar', [string]$Label = '7')
$ErrorActionPreference = 'Stop'
$headers = @{ Accept = 'application/json, text/event-stream' }
$init = @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{
    protocolVersion='2025-06-18'; capabilities=@{}; clientInfo=@{name='addon-query-comparison';version='1'}
}} | ConvertTo-Json -Depth 10 -Compress
$response = Invoke-WebRequest "$BaseUrl/mcp" -Method Post -ContentType application/json -Headers $headers -Body $init
$headers['Mcp-Session-Id'] = $response.Headers['Mcp-Session-Id'][0]
$headers['MCP-Protocol-Version'] = '2025-06-18'
function Rpc([string]$Method, [hashtable]$Parameters) {
    $body = @{jsonrpc='2.0'; id=2; method=$Method; params=$Parameters} | ConvertTo-Json -Depth 20 -Compress
    $reply = Invoke-RestMethod "$BaseUrl/mcp" -Method Post -ContentType application/json -Headers $headers -Body $body
    if ($reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
    return $reply.result
}
function Call([string]$Name, [hashtable]$Arguments) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = Rpc 'tools/call' @{name=$Name;arguments=$Arguments}
    $elapsed = $watch.Elapsed.TotalMilliseconds
    if ($result.isError) { throw $result.content[0].text }
    $text = $result.content[0].text
    [pscustomobject]@{data=($text | ConvertFrom-Json -Depth 80);bytes=[Text.Encoding]::UTF8.GetByteCount($text);elapsedMs=[math]::Round($elapsed,1)}
}
try {
    $catalog = @(); $cursor = $null
    do {
        $parameters = @{}; if ($cursor) { $parameters.cursor=$cursor }
        $page = Rpc 'tools/list' $parameters
        $catalog += @($page.tools); $cursor=$page.nextCursor
    } while ($cursor)
    if (!(($catalog | Where-Object name -EQ 'list_addon_elements').inputSchema.properties.detail)) {
        throw 'The running build does not expose addon query detail; verify /health.buildMvid.'
    }
    $full = Call 'list_addon_elements' @{addon=$Addon;allNodes=$true;maxNodes=500;detail='full'}
    $compact = Call 'list_addon_elements' @{addon=$Addon;allNodes=$true;maxNodes=500;detail='compact'}
    $filtered = Call 'list_addon_elements' @{addon=$Addon;label=$Label;maxNodes=20;detail='compact'}
    $expected = @($full.data.elements | Where-Object {$_.label -and $_.label.Trim().Equals($Label.Trim(),[StringComparison]::OrdinalIgnoreCase)})
    if ($full.data.truncated -or $compact.data.truncated -or $filtered.data.truncated) { throw 'Comparison needs an exhaustive scan; choose a smaller addon.' }
    if ($full.data.returned -ne $compact.data.returned -or $expected.Count -ne $filtered.data.returned -or $expected.Count -eq 0) {
        throw 'Query results differ from filtering the complete full result, or no label matched.'
    }
    function Identity($Element) { '{0}:{1}' -f ($Element.componentPath -join '/'), $Element.nodeId }
    $expectedKeys = @($expected | ForEach-Object { Identity $_ } | Sort-Object)
    $filteredKeys = @($filtered.data.elements | ForEach-Object { Identity $_ } | Sort-Object)
    if (Compare-Object $expectedKeys $filteredKeys) { throw 'Filtered paths differ from full-result paths.' }
    $verified = 0
    foreach ($element in $filtered.data.elements) {
        $snapshot = Call 'get_addon_node_snapshot' @{addon=$Addon;nodeIds=@($element.nodeId);componentPath=@($element.componentPath)}
        if (!$snapshot.data.available -or !$snapshot.data.nodes[0].available) { throw 'A discovered component path did not resolve.' }
        $verified++
    }
    [pscustomobject]@{
        buildMvid=(Invoke-RestMethod "$BaseUrl/health").buildMvid;addon=$Addon;label=$Label;catalogCount=$catalog.Count
        full=@{nodes=$full.data.returned;utf8Bytes=$full.bytes;elapsedMs=$full.elapsedMs}
        compact=@{nodes=$compact.data.returned;utf8Bytes=$compact.bytes;elapsedMs=$compact.elapsedMs}
        filtered=@{nodes=$filtered.data.returned;utf8Bytes=$filtered.bytes;elapsedMs=$filtered.elapsedMs}
        compactReductionPercent=[math]::Round(100*(1-$compact.bytes/$full.bytes),2)
        filteredReductionPercent=[math]::Round(100*(1-$filtered.bytes/$full.bytes),2)
        verifiedPaths=$verified
    } | ConvertTo-Json -Depth 10
} finally {
    Invoke-WebRequest "$BaseUrl/mcp" -Method Delete -Headers $headers | Out-Null
}
