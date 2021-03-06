﻿using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Builder.Feature;

namespace Stratis.Bitcoin.IntegrationTests.Common
{
    public static class OverrideServiceFeatureExtension
    {
        /// <summary>
        /// Adds a feature to the node that will allow secertain services to be overridden.
        /// </summary>
        /// <param name="fullNodeBuilder">The object used to build the current node.</param>
        /// <param name="serviceToOverride">Callback routine that will override a given service.</param>
        /// <typeparam name="T">The feature that the service will be replaced in.</typeparam>
        /// <returns>The full node builder, enriched with the new component.</returns>
        public static IFullNodeBuilder OverrideService<T>(this IFullNodeBuilder fullNodeBuilder, Action<IServiceCollection> serviceToOverride)
        {
            fullNodeBuilder.ConfigureFeature(features =>
            {
                IFeatureRegistration feature = features.FeatureRegistrations.FirstOrDefault(f => f.FeatureType == typeof(T));
                if (feature != null)
                {
                    feature.FeatureServices(services =>
                    {
                        serviceToOverride(services);
                    });
                }
            });

            return fullNodeBuilder;
        }
    }
}
