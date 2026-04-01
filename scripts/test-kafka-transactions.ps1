#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Kafka Transaction Test Script - Load fraud detection test transactions
    
.DESCRIPTION
    Sends test transactions to the Kafka 'transactions' topic and verifies
    fraud evaluation results in the 'fraud.decisions' topic.
    
.PARAMETER KafkaBroker
    Kafka broker address (default: localhost:29092)
    
.PARAMETER NumTransactions
    Number of test transactions to send (default: 10)
    
.PARAMETER Scenario
    Test scenario: 'normal', 'velocity', 'amount-anomaly', 'geo', 'all' (default: 'all')
    
.EXAMPLE
    .\test-kafka-transactions.ps1 -Scenario velocity -NumTransactions 5
#>

param(
    [string]$KafkaBroker = "localhost:29092",
    [int]$NumTransactions = 10,
    [ValidateSet('normal', 'velocity', 'amount-anomaly', 'geo', 'all')]
    [string]$Scenario = 'all'
)

# ════════════════════════════════════════════════════════════════════════════
# CONFIGURATION & HELPERS
# ════════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = "Stop"
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path

# Colors for output
$colors = @{
    Success = "`e[32m"  # Green
    Warning = "`e[33m"  # Yellow
    Error   = "`e[31m"  # Red
    Info    = "`e[34m"  # Blue
    Reset   = "`e[0m"   # Reset
}

function Write-Success { Write-Host "$($colors.Success)✓ $args$($colors.Reset)" }
function Write-Error-Custom { Write-Host "$($colors.Error)✗ $args$($colors.Reset)" -ForegroundColor Red }
function Write-Warning-Custom { Write-Host "$($colors.Warning)⚠ $args$($colors.Reset)" }
function Write-Info { Write-Host "$($colors.Info)ℹ $args$($colors.Reset)" }

# ════════════════════════════════════════════════════════════════════════════
# TRANSACTION GENERATORS
# ════════════════════════════════════════════════════════════════════════════

function New-TransactionEvent {
    param(
        [string]$TransactionId = [guid]::NewGuid().ToString(),
        [string]$UserId = "user_$([Random]::new().Next(1,100))",
        [decimal]$Amount = [decimal]([Random]::new().Next(10, 500)),
        [double]$Latitude = -33.8688 + ([Random]::new().NextDouble() * 0.1),
        [double]$Longitude = 151.2093 + ([Random]::new().NextDouble() * 0.1),
        [string]$MerchantId = "merch_$([Random]::new().Next(1, 50))",
        [datetime]$Timestamp = [datetime]::UtcNow
    )
    
    @{
        transactionId = $TransactionId
        userId        = $UserId
        amount        = $Amount
        currency      = "USD"
        merchantId    = $MerchantId
        locationLat   = $Latitude
        locationLng   = $Longitude
        timestamp     = $Timestamp.ToString("o")
    } | ConvertTo-Json -Compress
}

function GenerateNormalTransactions {
    Write-Info "Generating NORMAL transaction scenario (should ALLOW)"
    $transactions = @()
    
    for ($i = 0; $i -lt $NumTransactions; $i++) {
        $txn = New-TransactionEvent -UserId "user_normal" -Amount (100 + $i*10)
        $transactions += $txn
    }
    
    return $transactions
}

function GenerateVelocityTransactions {
    Write-Info "Generating VELOCITY rule scenario (threshold=5 in 300s, should BLOCK on 5th)"
    $transactions = @()
    $now = [datetime]::UtcNow
    
    for ($i = 0; $i -lt 5; $i++) {
        $txn = New-TransactionEvent `
            -TransactionId "txn_velocity_$i" `
            -UserId "user_velocity" `
            -Amount 99 `
            -Timestamp ($now.AddSeconds($i * 5))  # Spread across 20 seconds
        
        $transactions += $txn
    }
    
    return $transactions
}

function GenerateAmountAnomalyTransactions {
    Write-Info "Generating AMOUNT ANOMALY scenario (3x above avg, should REVIEW)"
    $transactions = @()
    $now = [datetime]::UtcNow
    
    # Create baseline (5 normal transactions)
    for ($i = 0; $i -lt 5; $i++) {
        $txn = New-TransactionEvent `
            -TransactionId "txn_anomaly_baseline_$i" `
            -UserId "user_anomaly" `
            -Amount 100  # $100 baseline
        
        $transactions += $txn
    }
    
    # Add anomalous transaction (3x = $300)
    $anomaly = New-TransactionEvent `
        -TransactionId "txn_anomaly_high" `
        -UserId "user_anomaly" `
        -Amount 350  # 3.5x average
    
    $transactions += $anomaly
    return $transactions
}

function GenerateGeoVelocityTransactions {
    Write-Info "Generating GEO-VELOCITY scenario (NYC -> LA in 30min, impossible travel)"
    $transactions = @()
    $now = [datetime]::UtcNow
    
    # Transaction in NYC
    $nyc = New-TransactionEvent `
        -TransactionId "txn_geo_nyc" `
        -UserId "user_geo" `
        -Latitude 40.7128 `
        -Longitude -74.0060 `
        -Timestamp $now
    
    $transactions += $nyc
    
    # Transaction in LA (30 min later) - ~4000km away
    $la = New-TransactionEvent `
        -TransactionId "txn_geo_la" `
        -UserId "user_geo" `
        -Latitude 34.0522 `
        -Longitude -118.2437 `
        -Timestamp $now.AddMinutes(30)  # Only 30 minutes later
    
    $transactions += $la
    return $transactions
}

# ════════════════════════════════════════════════════════════════════════════
# KAFKA OPERATIONS
# ════════════════════════════════════════════════════════════════════════════

function Test-KafkaConnectivity {
    Write-Info "Testing connection to Kafka broker: $KafkaBroker"
    
    try {
        $dockerCmd = "docker exec fraud-kafka kafka-broker-api-versions --bootstrap-server $KafkaBroker"
        Invoke-Expression $dockerCmd -ErrorAction SilentlyContinue > $null
        Write-Success "Kafka broker is accessible"
        return $true
    }
    catch {
        Write-Error-Custom "Cannot connect to Kafka at $KafkaBroker"
        Write-Error-Custom "Ensure docker-compose stack is running: docker compose up -d"
        return $false
    }
}

function Publish-KafkaMessages {
    param(
        [string[]]$Messages,
        [string]$Topic = "transactions"
    )
    
    Write-Info "Publishing $($Messages.Count) messages to topic: $Topic"
    
    $successCount = 0
    $failCount = 0
    
    foreach ($msg in $Messages) {
        try {
            $escapedMsg = $msg -replace '"', '\"'
            $dockerCmd = @"
                docker exec fraud-kafka kafka-console-producer \
                  --broker-list $KafkaBroker \
                  --topic $Topic <<< '$escapedMsg'
"@
            Invoke-Expression $dockerCmd -ErrorAction SilentlyContinue > $null
            $successCount++
            Write-Host "." -NoNewline -ForegroundColor Green
        }
        catch {
            $failCount++
            Write-Host "." -NoNewline -ForegroundColor Red
        }
    }
    
    Write-Host ""  # New line
    Write-Success "Published: $successCount/$($Messages.Count) messages"
    
    if ($failCount -gt 0) {
        Write-Warning-Custom "$failCount messages failed to publish"
    }
    
    return $successCount -eq $Messages.Count
}

function Get-KafkaMessages {
    param(
        [string]$Topic = "fraud.decisions",
        [int]$MaxMessages = 10,
        [int]$TimeoutSeconds = 5
    )
    
    Write-Info "Reading messages from topic: $Topic (max $MaxMessages, timeout ${TimeoutSeconds}s)"
    
    try {
        $dockerCmd = @"
            docker exec fraud-kafka timeout $TimeoutSeconds kafka-console-consumer \
              --bootstrap-server $KafkaBroker \
              --topic $Topic \
              --from-beginning \
              --max-messages $MaxMessages
"@
        $output = Invoke-Expression $dockerCmd -ErrorAction SilentlyContinue
        return $output
    }
    catch {
        Write-Warning-Custom "Could not read messages from Kafka"
        return $null
    }
}

function Show-TopicStats {
    param(
        [string]$Topic
    )
    
    try {
        $dockerCmd = "docker exec fraud-kafka kafka-consumer-groups --bootstrap-server $KafkaBroker --group fraud-engine --describe 2>/dev/null | grep $Topic"
        $output = Invoke-Expression $dockerCmd -ErrorAction SilentlyContinue
        
        if ($output) {
            Write-Info "Consumer lag for $Topic `: $output"
        }
    }
    catch { }
}

# ════════════════════════════════════════════════════════════════════════════
# MAIN EXECUTION
# ════════════════════════════════════════════════════════════════════════════

function Invoke-TestScenario {
    Write-Host "`n═══════════════════════════════════════════════════════════"
    Write-Host "   FRAUD DETECTION KAFKA TEST SCRIPT"
    Write-Host "═══════════════════════════════════════════════════════════`n"
    
    # Check Kafka connectivity
    if (-not (Test-KafkaConnectivity)) {
        exit 1
    }
    
    # Prepare test scenarios
    $allTransactions = @()
    
    if ($Scenario -in 'normal', 'all') {
        $allTransactions += GenerateNormalTransactions
    }
    
    if ($Scenario -in 'velocity', 'all') {
        $allTransactions += GenerateVelocityTransactions
    }
    
    if ($Scenario -in 'amount-anomaly', 'all') {
        $allTransactions += GenerateAmountAnomalyTransactions
    }
    
    if ($Scenario -in 'geo', 'all') {
        $allTransactions += GenerateGeoVelocityTransactions
    }
    
    # Publish transactions
    Write-Host "`n"
    Write-Info "=================== PUBLISHING TRANSACTIONS ==================="
    if (-not (Publish-KafkaMessages $allTransactions -Topic "transactions")) {
        Write-Error-Custom "Failed to publish all transactions"
    }
    
    # Wait for processing
    Write-Info "`nWaiting 3 seconds for fraud evaluation..."
    Start-Sleep -Seconds 3
    
    # Read results
    Write-Host "`n"
    Write-Info "=================== FRAUD DECISIONS RESULTS ==================="
    $decisions = Get-KafkaMessages -Topic "fraud.decisions" -MaxMessages 20 -TimeoutSeconds 5
    
    if ($decisions) {
        $decisions | ForEach-Object {
            try {
                $json = $_ | ConvertFrom-Json
                $decision = $json.Decision ?? "UNKNOWN"
                $riskScore = $json.RiskScore ?? "?"
                
                if ($decision -eq "BLOCK") {
                    Write-Host "  $($colors.Error)[$decision] Risk: $riskScore$($colors.Reset)" -ForegroundColor Red
                }
                elseif ($decision -eq "REVIEW") {
                    Write-Host "  $($colors.Warning)[$decision] Risk: $riskScore$($colors.Reset)" -ForegroundColor Yellow
                }
                else {
                    Write-Host "  $($colors.Success)[$decision] Risk: $riskScore$($colors.Reset)" -ForegroundColor Green
                }
            }
            catch { }
        }
    }
    else {
        Write-Warning-Custom "No fraud decisions received (check if fraud-engine consumer is running)"
    }
    
    # Show stats
    Write-Host "`n"
    Show-TopicStats "transactions"
    Show-TopicStats "fraud.decisions"
    
    Write-Host "`n═══════════════════════════════════════════════════════════"
    Write-Success "Test scenario '$Scenario' completed"
    Write-Host "═══════════════════════════════════════════════════════════`n"
}

# Run the test
Invoke-TestScenario
