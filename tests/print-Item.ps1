param(
    [Parameter(Mandatory = $true)]
    [int]$InventoryId
)

$BaseUrl = "http://localhost:7071"

Write-Host "Fetching item $InventoryId..." -ForegroundColor Cyan

$resp = Invoke-WebRequest -Method Get `
    -Uri "$BaseUrl/api/items/$InventoryId" `
    -ContentType "application/json" `
    -SkipHttpErrorCheck

if ($resp.StatusCode -ne 200) {
    Write-Host "Failed to fetch item. Status=$($resp.StatusCode)" -ForegroundColor Red
    Write-Host $resp.Content
    exit 1
}

$item = $resp.Content | ConvertFrom-Json

# Pretty print
Write-Host "InventoryId : $($item.inventoryId)"
Write-Host "PublicId    : $($item.publicId)"
Write-Host "SKU         : $($item.sku)"
Write-Host "Name        : $($item.name)"
Write-Host "Description : $($item.description)"
Write-Host "Qty On Hand : $($item.quantityOnHand)"
Write-Host "Unit Price  : $($item.unitPriceCents)"
Write-Host "IsActive    : $($item.isActive)"
Write-Host "IsDraft     : $($item.isDraft)"
Write-Host "IsDeleted   : $($item.isDeleted)"
Write-Host "CreatedAt   : $($item.createdAt)"
Write-Host "UpdatedAt   : $($item.updatedAt)"

if ($item.images) {
    Write-Host "`nImages:" -ForegroundColor DarkGray
    foreach ($img in $item.images) {
        Write-Host "  - ImageId=$($img.imageId) Primary=$($img.isPrimary) Order=$($img.sortOrder)"
        Write-Host "    Path=$($img.imagePath)"
    }
}
