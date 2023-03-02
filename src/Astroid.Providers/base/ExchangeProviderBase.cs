using System.ComponentModel;
using System.Reflection;
using Astroid.Core;
using Astroid.Entity;
using Newtonsoft.Json;

namespace Astroid.Providers;

public abstract class ExchangeProviderBase : IDisposable
{
	public IServiceProvider ServiceProvider { get; set; }
	protected AstroidDb Db { get; set; }
	protected ADExchange Exchange { get; set; }
	public string CorrelationId { get; set; }

	protected ExchangeProviderBase() { }

	protected ExchangeProviderBase(IServiceProvider serviceProvider, ADExchange exchange)
	{
		ServiceProvider = serviceProvider;
		// Db = serviceProvider.GetService(typeof(AstroidDb)) as AstroidDb ?? throw new Exception("Db cannot be null");
		Exchange = exchange;
		CorrelationId = GenerateCorrelationId();
	}

	public virtual void Context()
	{
		var propertyValues = JsonConvert.DeserializeObject<List<ProviderPropertyValue>>(Exchange.PropertiesJson);
		BindProperties(propertyValues);
	}

	public void BindProperties(List<ProviderPropertyValue> propertyValues)
	{
		if (propertyValues == null) throw new ArgumentException("There is no property to bind this provider.");

		var provider = this;
		var type = GetType();
		var properties = type.GetProperties().Where(x => x.GetCustomAttribute<PropertyMetadataAttribute>() != null).ToList();

		foreach (var property in properties)
		{
			var propertyValue = propertyValues.SingleOrDefault(p => p.Property == property.Name);
			if (propertyValue?.Value == null) continue;

			if (property.PropertyType == propertyValue.Value.GetType())
			{
				property.SetValue(provider, propertyValue.Value);
				continue;
			}

			try
			{
				if (property.PropertyType.IsGenericType)
				{
					var propertyValueString = $"{propertyValue.Value}";
					if (!string.IsNullOrWhiteSpace(propertyValueString))
					{
						property.SetValue(provider,
							JsonConvert.DeserializeObject(propertyValueString, property.PropertyType));
					}

					continue;
				}

				var converter = TypeDescriptor.GetConverter(property.PropertyType);
				var objValue = converter.ConvertFrom(propertyValue.Value.ToString());

				property.SetValue(provider, objValue);
				continue;
			}
			catch
			{
				// Unable to convert basic value
			}

			try
			{
				var value = JsonConvert.DeserializeObject(propertyValue.Value.ToString(), property.PropertyType);
				property.SetValue(provider, value);
			}
			catch
			{
				// Unable to convert complex value
			}
		}
	}

	public static string GenerateCorrelationId()
	{
		var random = new Random();
		var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
		return new string(Enumerable.Repeat(chars, 15)
			.Select(s => s[random.Next(s.Length)]).ToArray());
	}

	public abstract Task<AMProviderResult> ExecuteOrder(ADBot bot, AMOrderRequest order);

	public virtual void Dispose()
	{
		// Db.Dispose();
	}
}
