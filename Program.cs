using LinenLady.Inventory.Functions.Infrastructure.Sql;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication() // <-- use this instead of ConfigureFunctionsWorkerDefaults
    .ConfigureServices((ctx, services) =>
    {
        var sqlConnStr =
            ctx.Configuration["SQL_CONNECTION_STRING"]
            ?? ctx.Configuration.GetConnectionString("Sql")
            ?? throw new InvalidOperationException(
                "Missing SQL connection string (SQL_CONNECTION_STRING or ConnectionStrings:Sql).");

        // Repos (Infrastructure)
        services.AddScoped<IInventoryRepository>(_ => new InventoryRepository(sqlConnStr));
        services.AddScoped<IInventoryImageRepository>(_ => new InventoryImageRepository(sqlConnStr));
        services.AddScoped<IInventoryImagesQuery>(_ => new InventoryImagesQuery(sqlConnStr));

        // Handlers (Application)
        services.AddScoped<LinenLady.Inventory.Application.Items.CreateItemsHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Items.UpdateItemHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Items.DeleteItemHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.AddImagesHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.DeleteItemImageHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.GetImagesHandler>();

    })
    .Build();

host.Run();
