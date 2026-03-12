using LinenLady.Inventory.Functions.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication() // <-- use this instead of ConfigureFunctionsWorkerDefaults
    .ConfigureServices((ctx, services) =>
    {
        // AI Rewrite Service
        var aoaiEndpoint = ctx.Configuration["AZURE_OPENAI_ENDPOINT"]
            ?? throw new InvalidOperationException("Missing AZURE_OPENAI_ENDPOINT");
        var aoaiKey = ctx.Configuration["AZURE_OPENAI_API_KEY"]
            ?? throw new InvalidOperationException("Missing AZURE_OPENAI_API_KEY");
        var aoaiDeployment = ctx.Configuration["AZURE_OPENAI_DEPLOYMENT"]
            ?? throw new InvalidOperationException("Missing AZURE_OPENAI_DEPLOYMENT");
        var aoaiVersion = ctx.Configuration["AZURE_OPENAI_API_VERSION"] ?? "2024-02-15-preview";
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
        services.AddScoped<LinenLady.Inventory.Application.Images.AddImagesHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.GetImagesHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.SetPrimaryImageHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.ReplaceImageHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.SetPrimaryImageHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.NewBlobUrlHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Images.DeleteImageHandler>();

        services.AddScoped<LinenLady.Inventory.Application.Keywords.GenerateKeywordsHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Keywords.GenerateSeoHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Search.SimilarItemsHandler>();
        
        services.AddScoped<LinenLady.Inventory.Application.Items.CreateItemsHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Items.UpdateItemHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Items.SoftDeleteItemHandler>();
        services.AddSingleton<LinenLady.Inventory.Application.Items.IAiRewriteService>(
                    _ => new LinenLady.Inventory.Application.Items.AiRewriteService(
                        aoaiEndpoint, aoaiKey, aoaiDeployment, aoaiVersion));

        // Repos (Infrastructure)
        services.AddScoped<LinenLady.Infrastructure.Sql.ICustomerRepository>(
            _ => new LinenLady.Infrastructure.Sql.CustomerRepository(sqlConnStr));

        // Square HTTP client + service
        services.AddHttpClient("square");
        services.AddScoped<LinenLady.Infrastructure.ISquareService,
                        LinenLady.Infrastructure.SquareService>();

        // Customer + Reservation handlers (Application)
        services.AddScoped<LinenLady.Inventory.Application.Customers.SyncCustomerHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.GetMyProfileHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.UpdateProfileHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.UpsertAddressHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.DeleteAddressHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.SetPreferencesHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.CreateReservationHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.CancelReservationHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.SquareWebhookHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.ExpireReservationsHandler>();
        services.AddScoped<LinenLady.Inventory.Application.Customers.MessageHandler>();

    })
    .Build();

host.Run();
