using Content.Server.Ghost.Components;
using Content.Server.Singularity.Components;
using Content.Shared.Singularity;
using Content.Shared.Singularity.Components;
using JetBrains.Annotations;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Dynamics;

namespace Content.Server.Singularity.EntitySystems
{
    [UsedImplicitly]
    public class SingularitySystem : SharedSingularitySystem
    {
        [Dependency] private readonly IEntityLookup _lookup = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;

        /// <summary>
        /// How much energy the singulo gains from destroying a tile.
        /// </summary>
        private const int TileEnergyGain = 1;

        private const float GravityCooldown = 0.5f;
        private float _gravityAccumulator;

        private int _updateInterval = 1;
        private float _accumulator;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<ServerSingularityComponent, StartCollideEvent>(HandleCollide);
        }

        private void HandleCollide(EntityUid uid, ServerSingularityComponent component, StartCollideEvent args)
        {
            // This handles bouncing off of containment walls.
            // If you want the delete behavior we do it under DeleteEntities for reasons (not everything has physics).

            // If we're being deleted by another singularity, this call is probably for that singularity.
            // Even if not, just don't bother.
            if (component.BeingDeletedByAnotherSingularity)
                return;

            // Using this to also get smooth deletions is hard because we need to be hard for good bounce
            // off of containment but also we need to be non-hard so we can freely move through the station.
            // For now I've just made it so only the lookup does deletions and collision is just for fields.
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);
            _gravityAccumulator += frameTime;
            _accumulator += frameTime;

            while (_accumulator > _updateInterval)
            {
                _accumulator -= _updateInterval;

                foreach (var singularity in EntityManager.EntityQuery<ServerSingularityComponent>())
                {
                    singularity.Energy -= singularity.EnergyDrain;
                }
            }

            while (_gravityAccumulator > GravityCooldown)
            {
                _gravityAccumulator -= GravityCooldown;

                foreach (var singularity in EntityManager.EntityQuery<ServerSingularityComponent>())
                {
                    Update(singularity, GravityCooldown);
                }
            }
        }

        private void Update(ServerSingularityComponent component, float frameTime)
        {
            if (component.BeingDeletedByAnotherSingularity) return;

            var worldPos = EntityManager.GetComponent<TransformComponent>(component.Owner).WorldPosition;
            DestroyEntities(component, worldPos);
            DestroyTiles(component, worldPos);
            PullEntities(component, worldPos);
        }

        private float PullRange(ServerSingularityComponent component)
        {
            // Level 6 is normally 15 range but that's yuge.
            return 2 + component.Level * 2;
        }

        private float DestroyTileRange(ServerSingularityComponent component)
        {
            return component.Level - 0.5f;
        }

        private bool CanDestroy(SharedSingularityComponent component, EntityUid entity)
        {
            return entity == component.Owner ||
                   EntityManager.HasComponent<IMapGridComponent>(entity) ||
                   EntityManager.HasComponent<GhostComponent>(entity) ||
                   EntityManager.HasComponent<ContainmentFieldComponent>(entity) ||
                   EntityManager.HasComponent<ContainmentFieldGeneratorComponent>(entity);
        }

        private void HandleDestroy(ServerSingularityComponent component, EntityUid entity)
        {
            // TODO: Need singuloimmune tag
            if (CanDestroy(component, entity)) return;

            // Singularity priority management / etc.
            if (EntityManager.TryGetComponent<ServerSingularityComponent?>(entity, out var otherSingulo))
            {
                // MERGE
                if (!otherSingulo.BeingDeletedByAnotherSingularity)
                {
                    component.Energy += otherSingulo.Energy;
                }

                otherSingulo.BeingDeletedByAnotherSingularity = true;
            }

            EntityManager.QueueDeleteEntity(entity);

            if (EntityManager.TryGetComponent<SinguloFoodComponent?>(entity, out var singuloFood))
                component.Energy += singuloFood.Energy;
            else
                component.Energy++;
        }

        /// <summary>
        /// Handle deleting entities and increasing energy
        /// </summary>
        private void DestroyEntities(ServerSingularityComponent component, Vector2 worldPos)
        {
            // The reason we don't /just/ use collision is because we'll be deleting stuff that may not necessarily have physics (e.g. carpets).
            var destroyRange = DestroyTileRange(component);

            foreach (var entity in _lookup.GetEntitiesInRange(EntityManager.GetComponent<TransformComponent>(component.Owner).MapID, worldPos, destroyRange))
            {
                HandleDestroy(component, entity);
            }
        }

        private bool CanPull(EntityUid entity)
        {
            return !(EntityManager.HasComponent<GhostComponent>(entity) ||
                   EntityManager.HasComponent<IMapGridComponent>(entity) ||
                   EntityManager.HasComponent<MapComponent>(entity) ||
                   entity.IsInContainer());
        }

        private void PullEntities(ServerSingularityComponent component, Vector2 worldPos)
        {
            // TODO: When we split up dynamic and static trees we might be able to make items always on the broadphase
            // in which case we can just query dynamictree directly for brrt
            var pullRange = PullRange(component);
            var destroyRange = DestroyTileRange(component);

            foreach (var entity in _lookup.GetEntitiesInRange(EntityManager.GetComponent<TransformComponent>(component.Owner).MapID, worldPos, pullRange))
            {
                // I tried having it so level 6 can de-anchor. BAD IDEA, MASSIVE LAG.
                if (entity == component.Owner ||
                    !EntityManager.TryGetComponent<PhysicsComponent?>(entity, out var collidableComponent) ||
                    collidableComponent.BodyType == BodyType.Static) continue;

                if (!CanPull(entity)) continue;

                var vec = worldPos - EntityManager.GetComponent<TransformComponent>(entity).WorldPosition;

                if (vec.Length < destroyRange - 0.01f) continue;

                var speed = vec.Length * component.Level * collidableComponent.Mass;

                // Because tile friction is so high we'll just multiply by mass so stuff like closets can even move.
                collidableComponent.ApplyLinearImpulse(vec.Normalized * speed);
            }
        }

        /// <summary>
        /// Destroy any grid tiles within the relevant Level range.
        /// </summary>
        private void DestroyTiles(ServerSingularityComponent component, Vector2 worldPos)
        {
            var radius = DestroyTileRange(component);

            var circle = new Circle(worldPos, radius);
            var box = new Box2(worldPos - radius, worldPos + radius);

            foreach (var grid in _mapManager.FindGridsIntersecting(EntityManager.GetComponent<TransformComponent>(component.Owner).MapID, box))
            {
                foreach (var tile in grid.GetTilesIntersecting(circle))
                {
                    if (tile.Tile.IsEmpty) continue;
                    grid.SetTile(tile.GridIndices, Tile.Empty);
                    component.Energy += TileEnergyGain;
                }
            }
        }
    }
}
