﻿using System;
using System.Threading;
using System.Threading.Tasks;

using Tmds.DBus;

using Gazelle.AtSpi.DBus;
using Gazelle.Gudl;
using Gazelle.UiDom;

namespace Gazelle.AtSpi
{
    internal class AtSpiConnection : UiDomRoot
    {
        internal Connection connection;

        public override string DebugId => "AtSpiConnection";

        IRegistry registry;

        private AtSpiConnection(Connection connection, GudlStatement[] rules) : base(rules)
        {
            this.connection = connection;
        }

        internal static async Task<string> GetAtSpiBusAddress()
        {
            string result = Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS");
            // TODO: Try getting AT_SPI_BUS property from X11 root

            // Try getting bus address from session bus org.a11y.Bus interface
            if (string.IsNullOrWhiteSpace(result))
            {
                var session = Connection.Session;
                var launcher = session.CreateProxy<IBus>("org.a11y.Bus", "/org/a11y/bus");
                result = await launcher.GetAddressAsync();
            }
            return result;
        }

        internal static async Task<AtSpiConnection> Connect(GudlStatement[] config)
        {
            string bus = await GetAtSpiBusAddress();
            if (string.IsNullOrWhiteSpace(bus))
            {
                Console.WriteLine("AT-SPI bus could not be found. Did you enable assistive technologies in your desktop environment?");
                return null;
            }
            Console.WriteLine("AT-SPI bus found: {0}", bus);
            var options = new ClientConnectionOptions(bus);
            options.SynchronizationContext = SynchronizationContext.Current;
            var connection = new Connection(options);
            await connection.ConnectAsync();
            var result = new AtSpiConnection(connection, config);

            // Resolve the service name to an actual client. Signals will come from the client name, so
            // we need this to distinguish between signals from the AT-SPI root and signals from an
            // application's root, both of which use the object path "/org/a11y/atspi/accessible/root"
            string registryClient = await connection.ResolveServiceOwnerAsync("org.a11y.atspi.Registry");

            result.registry = connection.CreateProxy<IRegistry>(registryClient, "/org/a11y/atspi/registry");

            // Register all the events we're interested in at the start, fine-grained management isn't worth it
            await result.registry.RegisterEventAsync("object:children-changed");
            await result.registry.RegisterEventAsync("object:state-changed");
            await result.registry.RegisterEventAsync("object:property-change:accessible-role");

            result.AddChild(0, new AtSpiObject(result, registryClient, "/org/a11y/atspi/accessible/root"));

            return result;
        }
    }
}
