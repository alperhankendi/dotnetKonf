﻿using System;
using System.Linq;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.SystemData;

namespace Core.EventStore
{
    public class ProjectionManager
    {
        public static readonly ProjectionManagerBuilder With = new ProjectionManagerBuilder();
        
        private readonly IEventStoreConnection eventStoreConnection;
        private readonly ICheckpointStore checkpointStore;
        private readonly Projection[] projections;
        private readonly UserCredentials userCredentials;

        private readonly int maxLiveQueueSize ;
        private readonly int readBatchSize;
        private readonly bool verboseLogging;
        
        internal ProjectionManager(IEventStoreConnection eventStoreConnection, 
                                    ICheckpointStore checkpointStore,
                                    Projection[] projections,
                                    int maxLiveQueueSize,
                                    int readBatchSize,
                                    bool verboseLogging,
                                    UserCredentials userCredentials=null
                                  )
        {
            this.eventStoreConnection = eventStoreConnection ?? throw new ArgumentNullException(nameof(eventStoreConnection));
            this.checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
            this.projections = projections;
            this.userCredentials = userCredentials;

            this.maxLiveQueueSize = maxLiveQueueSize;
            this.readBatchSize = readBatchSize;
            this.verboseLogging = verboseLogging;
        }

        public Task StartAll() => Task.WhenAll(this.projections.Select(StartProjection));

        private async Task StartProjection(Projection projection)
        {

            var lastCheckpoint = checkpointStore.GetLastCheckpoint<Position>(projection);
            var catchUpSubscriptionSettings = new CatchUpSubscriptionSettings(
                maxLiveQueueSize,
                readBatchSize,
                true,
                false,
                projection.ToString());
            
            eventStoreConnection.SubscribeToAllFrom(lastCheckpoint, 
                                                catchUpSubscriptionSettings,
                                                eventAppeared(projection),
                                                LiveProcessingStarted(projection),
                                                SubscriptionDropped(projection),
                                                userCredentials);
        }

        //https://eventstore.org/blog/20130306/getting-started-part-3-subscriptions/
        //SubscribeToAllFrom(this IEventStoreConnection target,
        //                    Position? lastCheckpoint,
        //                    CatchUpSubscriptionSettings settings,
        //                    Action<EventStoreCatchUpSubscription, ResolvedEvent> eventAppeared,
        //                    Action<EventStoreCatchUpSubscription> liveProcessingStarted = null,
        //                    Action<EventStoreCatchUpSubscription, SubscriptionDropReason, Exception> subscriptionDropped = null,
        //                    UserCredentials userCredentials = null)
        
        private  Action<EventStoreCatchUpSubscription, ResolvedEvent> eventAppeared(Projection projection)
            =>  (_, e) =>
            {
                // check system events and ignore them...
                if (e.OriginalEvent.EventType.StartsWith("$")) return ;
                
                // find event type
                var eventType = EventTypeMapper.GetType(e.Event.EventType);

                if (eventType == null) return;
                // deserialize the event.

                var domainEvent = e.Deserialze();

                //build your projection
                projection.Handle(domainEvent);
                
                //store current checkpoint
                checkpointStore.SetCheckpoint(e.OriginalPosition.Value, projection);
                                
                Console.WriteLine($"{DateTime.UtcNow.Ticks}---{e.Event.EventStreamId}-{domainEvent} projected into {projection}-{e.OriginalPosition.Value}");
     
            };

        private Action<EventStoreCatchUpSubscription> LiveProcessingStarted(Projection projection) 
            => async (eventStoreCatchUpSubscription) =>
            {
                Console.WriteLine($"{projection} has been started,now processing real time!");
            };

        
        //https://github.com/EventStore/EventStore/issues/929
        //https://github.com/EventStore/EventStore/issues/1127
        //still open issue on EventStore...
        private Action<EventStoreCatchUpSubscription, SubscriptionDropReason, Exception> SubscriptionDropped(Projection projection)
            => async (eventStoreCatchUpSubscription, subscriptionDropReason, exception) =>
            {
                
                eventStoreCatchUpSubscription.Stop();

                switch (subscriptionDropReason)
                {
                    case SubscriptionDropReason.UserInitiated:
                        Console.WriteLine($"{projection} projection stopped by user.");
                        break;
                    case SubscriptionDropReason.SubscribingError:
                    case SubscriptionDropReason.ServerError:
                    case SubscriptionDropReason.ConnectionClosed:
                    case SubscriptionDropReason.CatchUpError:
                    case SubscriptionDropReason.ProcessingQueueOverflow:
                    case SubscriptionDropReason.EventHandlerException:
                        Console.WriteLine($"{projection} projection stopped because of a transient error ({subscriptionDropReason}). ");
                        Console.WriteLine($"Exception Detail:  {exception}");    
                        Console.WriteLine("Attempting to restart...");
                        await Task.Run(() => StartProjection(projection));
                        break;
                    default:
                        Console.WriteLine("Your subscription gg");
                        Console.WriteLine($"Exception Detail:  {exception}");    
                        break;
                }
            };
    }
}
