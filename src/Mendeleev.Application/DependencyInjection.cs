using FluentValidation;
using Mendeleev.Application.Abstractions.Behaviors;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Admin;
using Mendeleev.Application.Admin.Broadcasts;
using Mendeleev.Application.Admin.Users;
using Mendeleev.Application.Configuration;
using Mendeleev.Application.Notifications;
using Mendeleev.Application.Panel;
using Mendeleev.Application.Payments;
using Mendeleev.Application.Subscriptions.Trial;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.Scan(scan => scan.FromAssembliesOf(typeof(DependencyInjection))
                .AddClasses(classes => classes.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false)
                    .AsImplementedInterfaces()
                    .WithScopedLifetime()
                .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<>)), publicOnly: false)
                    .AsImplementedInterfaces()
                    .WithScopedLifetime()
                .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false)
                    .AsImplementedInterfaces()
                    .WithScopedLifetime());

            services.Decorate(typeof(ICommandHandler<,>), typeof(ValidationDecorator.CommandHandler<,>));
            services.Decorate(typeof(ICommandHandler<>), typeof(ValidationDecorator.CommandBaseHandler<>));

            services.Decorate(typeof(IQueryHandler<,>), typeof(LoggingDecorator.QueryHandler<,>));
            services.Decorate(typeof(ICommandHandler<,>), typeof(LoggingDecorator.CommandHandler<,>));
            services.Decorate(typeof(ICommandHandler<>), typeof(LoggingDecorator.CommandBaseHandler<>));

            // Domain event handlers are invoked by the outbox processor, not in-process after the save.
            services.Scan(scan => scan.FromAssembliesOf(typeof(DependencyInjection))
                .AddClasses(classes => classes.AssignableTo(typeof(IDomainEventHandler<>)), publicOnly: false)
                .AsImplementedInterfaces()
                .WithScopedLifetime());

            services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

            // Defaults here; Infrastructure binds the configuration sections over them.
            services.AddOptions<ServiceOptions>();
            services.AddOptions<SubscriptionOptions>();
            services.AddOptions<PaymentOptions>();

            services.AddScoped<INotificationScheduler, NotificationScheduler>();
            services.AddScoped<IPanelSynchronizer, PanelSynchronizer>();
            services.AddScoped<IPaymentApplier, PaymentApplier>();
            services.AddScoped<ITrialEligibility, TrialEligibility>();
            services.AddScoped<IStaffAuthorizer, StaffAuthorizer>();
            services.AddScoped<IUserCardBuilder, UserCardBuilder>();
            services.AddScoped<IBroadcastDeliveryService, BroadcastDeliveryService>();

            return services;
        }
    }
}
