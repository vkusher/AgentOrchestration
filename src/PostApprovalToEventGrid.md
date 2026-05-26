$ApprovalId = ''
$Endpoint   = ''
$Topic      = ''
$Key        = ''

$body = @{
  specversion = '1.0'
  id          = [Guid]::NewGuid().ToString()
  source      = 'https://reviewer.local'
  type        = 'agenthandoff.approval.decided'
  time        = (Get-Date).ToUniversalTime().ToString('o')
  datacontenttype = 'application/json'
  data = @{
    approvalId = $ApprovalId
    approved   = $true
    decidedBy  = 'vadim@test'
    reason     = 'manual smoke test'
  }
} | ConvertTo-Json -Depth 5

Invoke-RestMethod `
  -Method Post `
  -Uri "$Endpoint/topics/$Topic`:publish?api-version=2024-06-01" `
  -Headers @{ 'Authorization' = "SharedAccessKey $Key"; 'Content-Type' = 'application/cloudevents+json' } `
  -Body $body
