using Azure.Messaging.ServiceBus;
using BasketService.Models;
using BasketService.Repositories;
using Grpc.Core;
using GrpcBasket;
namespace BasketService;

public class BasketService(IBasketRepository repository, IConfiguration configuration, IServiceProvider serviceProvider, ILogger<BasketService> logger) : Basket.BasketBase
{
    private readonly IBasketRepository _repository = repository;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<BasketService> _logger = logger;
    private ServiceBusClient? _serviceBusClient;

    public override async Task<CustomerBasketResponse> GetBasketById(BasketRequest request, ServerCallContext context)
    {
        // Uncomment to force a delay for testing resiliency, etc.
        //await Task.Delay(5000);

        var data = await _repository.GetBasketAsync(request.Id);

        if (data != null)
        {
            return MapToCustomerBasketResponse(data);
        }
        else
        {
            context.Status = new Status(StatusCode.NotFound, $"Basket with id {request.Id} does not exist");
        }

        return new CustomerBasketResponse();
    }

    public override async Task<CustomerBasketResponse?> UpdateBasket(CustomerBasketRequest request, ServerCallContext context)
    {
        var customerBasket = MapToCustomerBasket(request);
        var response = await _repository.UpdateBasketAsync(customerBasket);

        if (response != null)
        {
            return MapToCustomerBasketResponse(response);
        }

        context.Status = new Status(StatusCode.NotFound, $"Basket with buyer id {request.BuyerId} do not exist");

        return null;
    }

    public override async Task<CheckoutCustomerBasketResponse> CheckoutBasket(CheckoutCustomerBasketRequest request, ServerCallContext context)
    {
        var buyerId = request.BuyerId;
        var basket = await _repository.GetBasketAsync(buyerId);

        if (basket is null)
        {
            context.Status = new Status(StatusCode.NotFound, $"Basket with id {buyerId} does not exist");
            return new();
        }

        var order = new Order()
        {
            Id = Guid.NewGuid().ToString(),
            BuyerId = buyerId,
            Items = basket.Items,
        };

        _logger.LogInformation("Checking out {Count} item(s) for BuyerId: {BuyerId}.", order.Items.Count, buyerId);

        _serviceBusClient ??= serviceProvider.GetService<ServiceBusClient>();
        if (_serviceBusClient is null)
        {
            _logger.LogWarning($"Azure ServiceBus is unavailable. Ensure you have configured it - for example in AppHosts's user secrets under {AspireServiceBusExtensions.DefaultNamespaceConfigKey}.");
        }
        else
        {
            const string configKeyName = "Aspire:Azure:Messaging:ServiceBus:OrderQueueName";
            string? queueName = _configuration[configKeyName];
            if (string.IsNullOrEmpty(queueName))
            {
                context.Status = new Status(StatusCode.Internal, $"Queue name not found. Please add a valid name for configuration key '{configKeyName}'.");
                return new();
            }

            await using ServiceBusSender sender = _serviceBusClient.CreateSender(queueName);

            var message = new ServiceBusMessage(new BinaryData(order));

            await sender.SendMessageAsync(message);
        }

        await _repository.DeleteBasketAsync(buyerId);

        _logger.LogInformation("Order Id {Id} submitted.", order.Id);

        return new CheckoutCustomerBasketResponse();
    }

    public override async Task<DeleteCustomerBasketResponse> DeleteBasket(DeleteCustomerBasketRequest request, ServerCallContext context)
    {
        await _repository.DeleteBasketAsync(request.BuyerId);
        return new DeleteCustomerBasketResponse();
    }

    private static CustomerBasketResponse MapToCustomerBasketResponse(CustomerBasket customerBasket)
    {
        var response = new CustomerBasketResponse
        {
            BuyerId = customerBasket.BuyerId
        };

        foreach (var item in customerBasket.Items)
        {
            response.Items.Add(new BasketItemResponse
            {
                Id = item.Id ?? Guid.NewGuid().ToString(),
                OldUnitPrice = item.OldUnitPrice,
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice
            });
        }

        return response;
    }

    private static CustomerBasket MapToCustomerBasket(CustomerBasketRequest customerBasketRequest)
    {
        var response = new CustomerBasket
        {
            BuyerId = customerBasketRequest.BuyerId
        };

        foreach (var item in customerBasketRequest.Items)
        {
            response.Items.Add(new BasketItem
            {
                Id = item.Id,
                OldUnitPrice = item.OldUnitPrice,
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice
            });
        }

        return response;
    }
}