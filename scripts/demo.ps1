<#
.SYNOPSIS
  End-to-end demo against a running SeatFlow stack (docker compose or kind).

.EXAMPLE
  ./scripts/demo.ps1                       # happy path: hold -> idempotent replay -> conflict -> pay -> confirmed -> e-mail
  ./scripts/demo.ps1 -Scenario race        # 20 users race for one seat, exactly one wins
  ./scripts/demo.ps1 -Scenario declined    # card declined -> seats released (compensation)
  ./scripts/demo.ps1 -Scenario late-payment # payment lands after the deadline -> automatic refund (compensation)
  ./scripts/demo.ps1 -Scenario all
#>
param(
    [string]$BaseUrl = "http://localhost:8080",
    [string]$MailpitUrl = "http://localhost:8025",
    [ValidateSet("happy", "race", "declined", "late-payment", "all")]
    [string]$Scenario = "happy"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.Net.Http

function Write-Step([string]$Text) { Write-Host ""; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "    OK  $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "        $Text" -ForegroundColor DarkGray }
function Fail([string]$Text) { Write-Host "    FAIL $Text" -ForegroundColor Red; exit 1 }

# Works on Windows PowerShell 5.1 and PowerShell 7: returns status, headers and parsed JSON body, never throws on 4xx.
function Invoke-Api {
    param([string]$Method, [string]$Path, [string]$Token, $Body, [hashtable]$Headers = @{})
    $client = New-Object System.Net.Http.HttpClient
    try {
        $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($Method)), "$BaseUrl$Path"
        if ($Token) { $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue "Bearer", $Token }
        foreach ($key in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key, [string]$Headers[$key]) }
        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json -Depth 10 -Compress
            $request.Content = New-Object System.Net.Http.StringContent $json, ([System.Text.Encoding]::UTF8), "application/json"
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $parsed = $null
        if ($text) { try { $parsed = $text | ConvertFrom-Json } catch { $parsed = $text } }
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $response.Headers; Body = $parsed }
    }
    finally { $client.Dispose() }
}

function Get-Token([string]$Email) {
    $r = Invoke-Api -Method POST -Path "/auth/token" -Body @{ email = $Email }
    if ($r.Status -ne 200) { Fail "could not get a token for $Email (HTTP $($r.Status))" }
    return $r.Body.access_token
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 500
    }
    Fail "timed out after ${TimeoutSeconds}s waiting for $What"
}

function Wait-BookingStatus([string]$Token, [string]$BookingId, [string[]]$Statuses, [int]$TimeoutSeconds = 30) {
    $script:lastStatus = $null
    return Wait-Until -TimeoutSeconds $TimeoutSeconds -What "booking $BookingId to reach $($Statuses -join '/')" -Condition {
        $b = (Invoke-Api -Method GET -Path "/api/bookings/$BookingId" -Token $Token).Body
        if ($b.status -ne $script:lastStatus) { Write-Info "status: $($b.status)"; $script:lastStatus = $b.status }
        if ($Statuses -contains $b.status) { return $b }
        return $null
    }
}

function Get-SeatMap([string]$EventId) { (Invoke-Api -Method GET -Path "/api/bookings/events/$EventId/seats").Body }

function Get-FreeSeats([string]$EventId, [int]$Count, [string]$Section = "A") {
    $map = Get-SeatMap $EventId
    $free = @($map.seats | Where-Object { $_.status -eq "Available" -and $_.section -eq $Section } | Select-Object -First $Count)
    if ($free.Count -lt $Count) { Fail "not enough free seats left in section $Section; run 'docker compose down -v' to reset" }
    # The leading comma stops PowerShell from unrolling a one-element array into a bare string.
    return , [string[]]@($free | ForEach-Object { $_.seatId })
}

function Hold-Seats([string]$Token, [string]$EventId, [string[]]$SeatIds, [string]$Key) {
    return Invoke-Api -Method POST -Path "/api/bookings" -Token $Token -Headers @{ "Idempotency-Key" = $Key } `
        -Body @{ eventId = $EventId; seatIds = @($SeatIds) }
}

function New-Key { [guid]::NewGuid().ToString() }

# ---------------------------------------------------------------------------------------------------------------

function Initialize-Demo {
    Write-Step "Waiting for the gateway at $BaseUrl"
    Wait-Until -TimeoutSeconds 120 -What "gateway readiness" -Condition {
        try { return (Invoke-Api -Method GET -Path "/health/ready").Status -eq 200 } catch { return $false }
    } | Out-Null
    Write-Ok "gateway is ready"

    Write-Step "Fetching the catalog (anonymous, served from HybridCache/Redis after the first call)"
    $events = (Invoke-Api -Method GET -Path "/api/catalog/events").Body
    if (-not $events) { Fail "the catalog has no events" }
    $script:event = $events[0]
    Write-Ok "$($events.Count) upcoming event(s); using '$($script:event.title)' ($($script:event.id))"

    Write-Step "Waiting for Booking to receive the event over RabbitMQ (catalog outbox -> booking inbox)"
    Wait-Until -TimeoutSeconds 60 -What "the seat map" -Condition {
        (Invoke-Api -Method GET -Path "/api/bookings/events/$($script:event.id)/seats").Status -eq 200
    } | Out-Null
    $map = Get-SeatMap $script:event.id
    Write-Ok "seat map: $($map.available) available, $($map.held) held, $($map.sold) sold"
}

function Invoke-HappyPath {
    $alice = Get-Token "alice@example.com"
    $bob = Get-Token "bob@example.com"
    $seats = Get-FreeSeats $script:event.id 2

    Write-Step "Alice holds $($seats -join ', ')"
    $key = New-Key
    $hold = Hold-Seats $alice $script:event.id $seats $key
    if ($hold.Status -ne 201) { Fail "expected 201, got $($hold.Status): $($hold.Body | ConvertTo-Json -Compress)" }
    $booking = $hold.Body
    Write-Ok "201 Created, booking $($booking.id), total $($booking.total) $($booking.currency), hold expires $($booking.holdExpiresAt)"

    Write-Step "Alice's client retries the same request (network blip) with the same Idempotency-Key"
    $replay = Hold-Seats $alice $script:event.id $seats $key
    $replayed = $replay.Headers.Contains("Idempotent-Replayed")
    if ($replay.Status -ne 200 -or $replay.Body.id -ne $booking.id -or -not $replayed) { Fail "expected a 200 replay of $($booking.id), got $($replay.Status)" }
    Write-Ok "200 OK, same booking id, Idempotent-Replayed: true (no second booking)"

    Write-Step "Bob tries to grab $($seats[0])"
    $conflict = Hold-Seats $bob $script:event.id @($seats[0]) (New-Key)
    if ($conflict.Status -ne 409) { Fail "expected 409, got $($conflict.Status)" }
    Write-Ok "409 Conflict, unavailable: $($conflict.Body.seats -join ', ')"

    Write-Step "Alice checks out with tok_visa (booking -> outbox -> payment.process -> fake provider -> payment.succeeded)"
    $checkout = Invoke-Api -Method POST -Path "/api/bookings/$($booking.id)/checkout" -Token $alice -Body @{ paymentToken = "tok_visa" }
    if ($checkout.Status -ne 202) { Fail "expected 202, got $($checkout.Status)" }
    Write-Ok "202 Accepted, status $($checkout.Body.status)"
    $confirmed = Wait-BookingStatus $alice $booking.id @("Confirmed", "PaymentFailed", "Expired")
    if ($confirmed.status -ne "Confirmed") { Fail "booking ended as $($confirmed.status): $($confirmed.statusReason)" }
    Write-Ok "booking Confirmed"

    $map = Get-SeatMap $script:event.id
    $sold = @($map.seats | Where-Object { $seats -contains $_.seatId -and $_.status -eq "Sold" })
    if ($sold.Count -ne $seats.Count) { Fail "seats are not marked Sold on the seat map" }
    Write-Ok "seat map shows $($seats -join ', ') as Sold"

    $payment = (Invoke-Api -Method GET -Path "/api/payments/$($booking.id)" -Token $alice).Body
    Write-Ok "payment $($payment.id): $($payment.status) $($payment.amount) $($payment.currency)"

    Write-Step "Notification service (Python) e-mails the ticket (checking Mailpit)"
    $mail = Find-Mail "alice@example.com" $booking.id
    if (-not $mail) { Fail "no confirmation e-mail for $($booking.id) in Mailpit ($MailpitUrl)" }
    Write-Ok "e-mail delivered: '$($mail.Subject)'"
}

function Find-Mail([string]$To, [string]$BookingId) {
    try {
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            $result = Invoke-RestMethod -Uri "$MailpitUrl/api/v1/search?query=$([uri]::EscapeDataString("to:$To $BookingId"))" -UseBasicParsing
            if ($result.messages_count -gt 0) { return $result.messages[0] }
            Start-Sleep -Milliseconds 500
        }
    }
    catch { return $null }
    return $null
}

function Invoke-Race {
    $seat = (Get-FreeSeats $script:event.id 1 "VIP")[0]
    $users = 20
    Write-Step "$users different users try to hold $seat at the same moment"
    $tokens = 1..$users | ForEach-Object { Get-Token "racer$_@example.com" }

    $client = New-Object System.Net.Http.HttpClient
    $body = (@{ eventId = $script:event.id; seatIds = @($seat) } | ConvertTo-Json -Compress)
    $tasks = foreach ($token in $tokens) {
        $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::Post), "$BaseUrl/api/bookings"
        $request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue "Bearer", $token
        [void]$request.Headers.TryAddWithoutValidation("Idempotency-Key", (New-Key))
        $request.Content = New-Object System.Net.Http.StringContent $body, ([System.Text.Encoding]::UTF8), "application/json"
        $client.SendAsync($request)
    }
    [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$tasks)
    $codes = $tasks | ForEach-Object { [int]$_.Result.StatusCode }
    $client.Dispose()

    $won = @($codes | Where-Object { $_ -eq 201 }).Count
    $lost = @($codes | Where-Object { $_ -eq 409 }).Count
    Write-Info "responses: $(($codes | Group-Object | ForEach-Object { "$($_.Count) x $($_.Name)" }) -join ', ')"
    if ($won -ne 1 -or $lost -ne ($users - 1)) { Fail "expected exactly one winner, got $won x 201 and $lost x 409" }
    Write-Ok "exactly one hold succeeded, $lost got 409"
}

function Invoke-Declined {
    $carol = Get-Token "carol@example.com"
    $seats = Get-FreeSeats $script:event.id 1 "B"
    Write-Step "Carol holds $($seats[0]) and pays with tok_declined"
    $booking = (Hold-Seats $carol $script:event.id $seats (New-Key)).Body
    Invoke-Api -Method POST -Path "/api/bookings/$($booking.id)/checkout" -Token $carol -Body @{ paymentToken = "tok_declined" } | Out-Null
    $final = Wait-BookingStatus $carol $booking.id @("PaymentFailed", "Confirmed")
    if ($final.status -ne "PaymentFailed") { Fail "expected PaymentFailed, got $($final.status)" }
    Write-Ok "PaymentFailed: $($final.statusReason)"
    $seat = (Get-SeatMap $script:event.id).seats | Where-Object { $_.seatId -eq $seats[0] }
    if ($seat.status -ne "Available") { Fail "seat was not released" }
    Write-Ok "compensation: $($seats[0]) is Available again"
}

function Invoke-LatePayment {
    $dave = Get-Token "dave@example.com"
    $seats = Get-FreeSeats $script:event.id 1 "B"
    Write-Step "Dave holds $($seats[0]) and pays with tok_slow (provider answers after the 15 s payment deadline)"
    $booking = (Hold-Seats $dave $script:event.id $seats (New-Key)).Body
    Invoke-Api -Method POST -Path "/api/bookings/$($booking.id)/checkout" -Token $dave -Body @{ paymentToken = "tok_slow" } | Out-Null
    $expired = Wait-BookingStatus $dave $booking.id @("Expired", "Confirmed") 40
    Write-Ok "saga timeout fired: $($expired.status) ($($expired.statusReason)); the seat went back on sale"
    $final = Wait-BookingStatus $dave $booking.id @("Refunded") 60
    Write-Ok "late payment.succeeded -> payment.refund -> payment.refunded: $($final.status)"
    $payment = (Invoke-Api -Method GET -Path "/api/payments/$($booking.id)" -Token $dave).Body
    Write-Ok "payment $($payment.id) is $($payment.status): $($payment.refundReason)"
}

Initialize-Demo
switch ($Scenario) {
    "happy" { Invoke-HappyPath }
    "race" { Invoke-Race }
    "declined" { Invoke-Declined }
    "late-payment" { Invoke-LatePayment }
    "all" { Invoke-HappyPath; Invoke-Race; Invoke-Declined; Invoke-LatePayment }
}
Write-Host ""
Write-Host "Demo '$Scenario' passed." -ForegroundColor Green
