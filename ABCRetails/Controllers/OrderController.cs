using ABCRetails.Models;
using ABCRetails.Models.ViewModels;
using ABCRetails.Services;
using Azure;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace ABCRetails.Controllers
{
    public class OrderController : Controller
    {
        private readonly IAzureStorageService _storageService;

        public OrderController(IAzureStorageService storageService)
        {
            _storageService = storageService;
        }

        public async Task<IActionResult> Index()
        {
            var orders = await _storageService.GetAllEntitiesAsync<Order>();
            return View(orders);
        }

        public async Task<IActionResult> Create()
        {
            var viewModel = new OrderCreateViewModel
            {
                Customers = await _storageService.GetAllEntitiesAsync<Customer>(),
                Products = await _storageService.GetAllEntitiesAsync<Product>(),
                // Fix: Specify the DateTimeKind as UTC for the initial date
                OrderDate = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc)
            };
            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(OrderCreateViewModel model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    var customer = await _storageService.GetEntityAsync<Customer>("Customer", model.CustomerId);
                    var product = await _storageService.GetEntityAsync<Product>("Product", model.ProductId);

                    if (customer == null || product == null)
                    {
                        ModelState.AddModelError("", "Invalid customer or product selected.");
                        await PopulateDropdowns(model);
                        return View(model);
                    }

                    if (product.StockAvailable < model.Quantity)
                    {
                        ModelState.AddModelError("Quantity", $"Insufficient stock. Available: {product.StockAvailable}");
                        await PopulateDropdowns(model);
                        return View(model);
                    }

                    // Fix: Ensure the incoming date is always treated as UTC before creating the entity.
                    var utcOrderDate = DateTime.SpecifyKind(model.OrderDate, DateTimeKind.Utc);

                    var order = new Order
                    {
                        // Ensure a new unique ID is generated for each new order
                        RowKey = Guid.NewGuid().ToString(),
                        CustomerId = model.CustomerId,
                        Username = customer.Username,
                        ProductId = model.ProductId,
                        ProductName = product.ProductName,
                        Quantity = model.Quantity,
                        OrderDate = utcOrderDate, // Use the corrected UTC date
                        UnitPrice = product.Price,
                        TotalPrice = product.Price * model.Quantity,
                        Status = model.Status,
                    };

                    await _storageService.AddEntityAsync(order);

                    // Update product stock
                    product.StockAvailable -= model.Quantity;
                    await _storageService.UpdateEntityAsync(product);

                    // Send events/messages
                    var orderMessage = new
                    {
                        OrderId = order.OrderId,
                        CustomerId = order.CustomerId,
                        CustomerName = $"{customer.Name} {customer.Surname}",
                        ProductName = product.ProductName,
                        Quantity = order.Quantity,
                        TotalPrice = order.TotalPrice,
                        OrderDate = order.OrderDate,
                        Status = order.Status
                    };
                    await _storageService.SendMessageAsync("order-notifications", JsonSerializer.Serialize(orderMessage));

                    var stockMessage = new
                    {
                        ProductId = product.ProductId,
                        ProductName = product.ProductName,
                        PreviousStock = product.StockAvailable + model.Quantity,
                        NewStockAvailable = product.StockAvailable,
                        UpdateBy = "OrderPlaced",
                        UpdateDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc) // Use UTC with proper Kind
                    };
                    await _storageService.SendMessageAsync("stock-updates", JsonSerializer.Serialize(stockMessage));

                    TempData["Success"] = "Order created successfully!";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", $"Error creating order: {ex.Message}");
                }
            }

            await PopulateDropdowns(model);
            return View(model);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return NotFound();
            }
            var order = await _storageService.GetEntityAsync<Order>("Order", id);
            if (order == null)
            {
                return NotFound();
            }
            return View(order);
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return NotFound();
            }
            var order = await _storageService.GetEntityAsync<Order>("Order", id);
            if (order == null)
            {
                return NotFound();
            }

            // Get customer and product for dropdowns
            var customer = await _storageService.GetEntityAsync<Customer>("Customer", order.CustomerId);
            var product = await _storageService.GetEntityAsync<Product>("Product", order.ProductId);

            var viewModel = new OrderEditViewModel
            {
                Order = order,
                Customers = await _storageService.GetAllEntitiesAsync<Customer>(),
                Products = await _storageService.GetAllEntitiesAsync<Product>(),
                StatusOptions = new List<string> { "Submitted", "Processing", "Shipped", "Delivered", "Cancelled" }
            };

            // Ensure we're working with UTC date in the view
            if (order.OrderDate.Kind != DateTimeKind.Utc)
            {
                order.OrderDate = DateTime.SpecifyKind(order.OrderDate, DateTimeKind.Utc);
            }

            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, OrderEditViewModel viewModel)
        {
            if (id != viewModel.Order.RowKey)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    // Get the existing order with latest ETag
                    var existingOrder = await _storageService.GetEntityAsync<Order>("Order", id);
                    if (existingOrder == null)
                    {
                        ModelState.AddModelError("", "Order not found or already deleted.");
                        await PopulateEditDropdowns(viewModel);
                        return View(viewModel);
                    }

                    // Get the original product to restore stock (with latest ETag)
                    var originalProduct = await _storageService.GetEntityAsync<Product>("Product", existingOrder.ProductId);
                    if (originalProduct == null)
                    {
                        ModelState.AddModelError("", "Original product not found.");
                        await PopulateEditDropdowns(viewModel);
                        return View(viewModel);
                    }

                    // Get the selected product for price calculation (with latest ETag)
                    var selectedProduct = await _storageService.GetEntityAsync<Product>("Product", viewModel.Order.ProductId);
                    if (selectedProduct == null)
                    {
                        ModelState.AddModelError("", "Selected product not found.");
                        await PopulateEditDropdowns(viewModel);
                        return View(viewModel);
                    }

                    // Check if product has changed
                    bool productChanged = existingOrder.ProductId != viewModel.Order.ProductId;

                    // Check if sufficient stock is available for the new product
                    if (productChanged && selectedProduct.StockAvailable < viewModel.Order.Quantity)
                    {
                        ModelState.AddModelError("Order.Quantity", $"Insufficient stock for {selectedProduct.ProductName}. Available: {selectedProduct.StockAvailable}");
                        await PopulateEditDropdowns(viewModel);
                        return View(viewModel);
                    }

                    // Check if quantity increased for same product
                    if (!productChanged && viewModel.Order.Quantity > existingOrder.Quantity)
                    {
                        int additionalQuantity = viewModel.Order.Quantity - existingOrder.Quantity;
                        if (selectedProduct.StockAvailable < additionalQuantity)
                        {
                            ModelState.AddModelError("Order.Quantity", $"Insufficient stock. Available: {selectedProduct.StockAvailable}");
                            await PopulateEditDropdowns(viewModel);
                            return View(viewModel);
                        }
                    }

                    // Handle stock updates based on the scenario
                    if (productChanged)
                    {
                        // Scenario 1: Product changed - restore original product stock, deduct from new product
                        originalProduct.StockAvailable += existingOrder.Quantity; // Restore original product stock
                        await _storageService.UpdateEntityAsync(originalProduct);

                        selectedProduct.StockAvailable -= viewModel.Order.Quantity; // Deduct from new product
                        await _storageService.UpdateEntityAsync(selectedProduct);
                    }
                    else
                    {
                        // Scenario 2: Same product, quantity changed
                        int quantityDifference = existingOrder.Quantity - viewModel.Order.Quantity;
                        selectedProduct.StockAvailable += quantityDifference; // Add back if quantity decreased, deduct if increased
                        await _storageService.UpdateEntityAsync(selectedProduct);
                    }

                    // Update order properties - use the existingOrder that has the correct ETag
                    existingOrder.ProductId = viewModel.Order.ProductId;
                    existingOrder.ProductName = selectedProduct.ProductName;
                    existingOrder.Quantity = viewModel.Order.Quantity;
                    existingOrder.OrderDate = viewModel.Order.OrderDate;
                    existingOrder.Status = viewModel.Order.Status;
                    existingOrder.UnitPrice = selectedProduct.Price;
                    existingOrder.TotalPrice = selectedProduct.Price * viewModel.Order.Quantity;

                    // Ensure OrderDate is UTC
                    if (existingOrder.OrderDate.Kind != DateTimeKind.Utc)
                    {
                        existingOrder.OrderDate = DateTime.SpecifyKind(existingOrder.OrderDate, DateTimeKind.Utc);
                    }

                    await _storageService.UpdateEntityAsync(existingOrder);

                    // Send stock update messages
                    if (productChanged)
                    {
                        var originalStockMessage = new
                        {
                            ProductId = originalProduct.ProductId,
                            ProductName = originalProduct.ProductName,
                            PreviousStock = originalProduct.StockAvailable - existingOrder.Quantity,
                            NewStockAvailable = originalProduct.StockAvailable,
                            UpdateBy = "OrderUpdated",
                            UpdateDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)
                        };
                        await _storageService.SendMessageAsync("stock-updates", JsonSerializer.Serialize(originalStockMessage));

                        var newStockMessage = new
                        {
                            ProductId = selectedProduct.ProductId,
                            ProductName = selectedProduct.ProductName,
                            PreviousStock = selectedProduct.StockAvailable + viewModel.Order.Quantity,
                            NewStockAvailable = selectedProduct.StockAvailable,
                            UpdateBy = "OrderUpdated",
                            UpdateDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)
                        };
                        await _storageService.SendMessageAsync("stock-updates", JsonSerializer.Serialize(newStockMessage));
                    }
                    else
                    {
                        var stockMessage = new
                        {
                            ProductId = selectedProduct.ProductId,
                            ProductName = selectedProduct.ProductName,
                            PreviousStock = selectedProduct.StockAvailable + (existingOrder.Quantity - viewModel.Order.Quantity),
                            NewStockAvailable = selectedProduct.StockAvailable,
                            UpdateBy = "OrderUpdated",
                            UpdateDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)
                        };
                        await _storageService.SendMessageAsync("stock-updates", JsonSerializer.Serialize(stockMessage));
                    }

                    TempData["Success"] = "Order updated successfully!";
                    return RedirectToAction(nameof(Index));
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    ModelState.AddModelError("", "The order has been modified by another user. Please refresh and try again.");
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", $"Error updating order: {ex.Message}");
                }
            }

            await PopulateEditDropdowns(viewModel);
            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            try
            {
                // Get the order first to restore stock
                var order = await _storageService.GetEntityAsync<Order>("Order", id);
                if (order != null)
                {
                    // Get the product and restore stock
                    var product = await _storageService.GetEntityAsync<Product>("Product", order.ProductId);
                    if (product != null)
                    {
                        product.StockAvailable += order.Quantity;
                        await _storageService.UpdateEntityAsync(product);

                        // Send stock update message
                        var stockMessage = new
                        {
                            ProductId = product.ProductId,
                            ProductName = product.ProductName,
                            PreviousStock = product.StockAvailable - order.Quantity,
                            NewStockAvailable = product.StockAvailable,
                            UpdateBy = "OrderDeleted",
                            UpdateDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)
                        };
                        await _storageService.SendMessageAsync("stock-updates", JsonSerializer.Serialize(stockMessage));
                    }
                }

                await _storageService.DeleteEntityAsync<Order>("Order", id);
                TempData["Success"] = "Order deleted successfully!";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Error deleting order: {ex.Message}";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<JsonResult> GetProductPrice(string productId)
        {
            try
            {
                var product = await _storageService.GetEntityAsync<Product>("Product", productId);
                if (product != null)
                {
                    return Json(new
                    {
                        success = true,
                        price = product.Price,
                        stock = product.StockAvailable,
                        productName = product.ProductName,
                    });
                }

                return Json(new { success = false });
            }
            catch
            {
                return Json(new { success = false });
            }
        }

        [HttpPost]
        public async Task<JsonResult> UpdateOrderStatus(string id, string newStatus)
        {
            try
            {
                var order = await _storageService.GetEntityAsync<Order>("Order", id);
                if (order == null)
                {
                    return Json(new { success = false, message = "Order not found" });
                }

                var previousStatus = order.Status;
                order.Status = newStatus;
                await _storageService.UpdateEntityAsync(order);

                var statusMessage = new
                {
                    OrderId = order.OrderId,
                    CustomerId = order.CustomerId,
                    CustomerName = order.Username,
                    ProductName = order.ProductName,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    UpdateDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc), // Use UTC with proper Kind
                    UpdateBy = "System"
                };

                await _storageService.SendMessageAsync("order-status-updates", JsonSerializer.Serialize(statusMessage));
                return Json(new { success = true, message = "Order status updated successfully!" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<JsonResult> GetProductImage(string productId)
        {
            try
            {
                var product = await _storageService.GetEntityAsync<Product>("Product", productId);
                if (product != null && !string.IsNullOrEmpty(product.ImageUrl))
                {
                    return Json(new
                    {
                        success = true,
                        imageUrl = product.ImageUrl
                    });
                }

                return Json(new { success = false });
            }
            catch
            {
                return Json(new { success = false });
            }
        }

        private async Task PopulateDropdowns(OrderCreateViewModel model)
        {
            model.Customers = await _storageService.GetAllEntitiesAsync<Customer>();
            model.Products = await _storageService.GetAllEntitiesAsync<Product>();
        }

        private async Task PopulateEditDropdowns(OrderEditViewModel model)
        {
            model.Customers = await _storageService.GetAllEntitiesAsync<Customer>();
            model.Products = await _storageService.GetAllEntitiesAsync<Product>();
            model.StatusOptions = new List<string> { "Submitted", "Processing", "Shipped", "Delivered", "Cancelled" };
        }
    }
}