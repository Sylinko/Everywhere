using System.Diagnostics.Metrics;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Configuration;

public abstract class SettingsBase(IServiceProvider serviceProvider) : ObservableObject
{
    protected static readonly Meter Meter = new(typeof(SettingsBase).FullName.NotNull(), RuntimeConstants.Version.ToString());

    protected IServiceProvider ServiceProvider { get; } = serviceProvider;

    protected T GetRequiredService<T>() where T : class => ServiceProvider.GetRequiredService<T>();
}