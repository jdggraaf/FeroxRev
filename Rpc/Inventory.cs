﻿#region using directives

using System.Threading.Tasks;
using POGOProtos.Data.Player;
using POGOProtos.Inventory.Item;
using POGOProtos.Networking.Requests;
using POGOProtos.Networking.Requests.Messages;
using POGOProtos.Networking.Responses;
using System.Collections.Generic;
using Google.Protobuf;
using System;
using PokemonGo.RocketAPI.Helpers;
using POGOProtos.Inventory;
using System.Linq;
using POGOProtos.Data;
using System.Collections.Concurrent;

#endregion

namespace PokemonGo.RocketAPI.Rpc
{
    public delegate void OnInventoryUpdateHandler();

    public class Inventory : BaseRpc
    {
        public event OnInventoryUpdateHandler OnInventoryUpdated;
        public ConcurrentDictionary<string, InventoryItem> InventoryItems = new ConcurrentDictionary<string, InventoryItem>();

        public Inventory(Client client) : base(client)
        {
        }

        private static string GetPokemonHashKey(ulong id)
        {
            return "PokemonData."+id;
        }

        private static string GetInventoryItemHashKey(InventoryItem item)
        {
            if (item == null || item.InventoryItemData == null)
                return null;

            var delta = item.InventoryItemData;

            if (delta.AppliedItems != null)
                return "AppliedItems";

            if (delta.AvatarItem != null)
                return "AvatarItem."+delta.AvatarItem.AvatarTemplateId;

            if (delta.Candy != null)
                return "Candy."+delta.Candy.FamilyId;

            if (delta.EggIncubators != null)
                return "EggIncubators";

            if (delta.InventoryUpgrades != null)
                return "InventoryUpgrades";

            if (delta.Item != null)
                return "Item."+delta.Item.ItemId;

            if (delta.PlayerCamera != null)
                return "PlayerCamera";

            if (delta.PlayerCurrency != null)
                return "PlayerCurrency";

            if (delta.PlayerStats != null)
                return "PlayerStats";

            if (delta.PokedexEntry != null)
                return "PokedexEntry."+delta.PokedexEntry.PokemonId;

            if (delta.PokemonData != null)
                return GetPokemonHashKey(delta.PokemonData.Id);

            if (delta.Quest != null)
                return "Quest."+delta.Quest.QuestType;

            throw new Exception("Unexpected inventory error. Could not generate hash code.");
        }

        private bool RemoveInventoryItem(InventoryItem item)
        {
            if (item == null)
                return false;

            return RemoveInventoryItem(GetInventoryItemHashKey(item));
        }

        private bool RemoveInventoryItem(string key)
        {
#pragma warning disable IDE0018 // Inline variable declaration - Build.Bat Error Happens if We Do
            InventoryItem toRemove;
            try
            {
                return InventoryItems.TryRemove(key, out toRemove);
            }
            catch (ArgumentNullException)
            {
                return false;
            }
#pragma warning restore IDE0018 // Inline variable declaration - Build.Bat Error Happens if We Do
        }

        private void AddRemoveOrUpdateItem(InventoryItem item)
        {
            if (item == null)
                return;

            if (item.DeletedItem != null)
            {
                // Items with DeletedItem have a null InventoryItemData and are not added to inventory.
                // But we still need to remove the pokemon with Id == item.DeletedItem.PokemonId from the inventory.
                var pokemonToRemoveKey = $"PokemonData.{item.DeletedItem.PokemonId}"; // Manually construct key.
                RemoveInventoryItem(pokemonToRemoveKey);
            }
            else
            {
                InventoryItems.AddOrUpdate(GetInventoryItemHashKey(item), item, (key, oldItem) =>
                {
                    // Check timestamps to make sure we update with a newer item.
                    if (oldItem.ModifiedTimestampMs < item.ModifiedTimestampMs)
                    {
                        // Copy fields over to the old item.
                        oldItem.InventoryItemData = item.InventoryItemData;
                        oldItem.ModifiedTimestampMs = item.ModifiedTimestampMs;
                    }

                    return oldItem;
                });
            }
        }

        public void MergeWith(GetInventoryResponse update)
        {
            var delta = update.InventoryDelta;

            if (delta?.InventoryItems == null)
            {
                return;
            }

            foreach(var item in delta.InventoryItems)
            {
                AddRemoveOrUpdateItem(item);
            }
            
            OnInventoryUpdated?.Invoke();
        }

        internal void RemoveInventoryItems(IEnumerable<InventoryItem> items)
        {   
            foreach (var item in items)
            {
                RemoveInventoryItem(item);
            }
        }

        public IEnumerable<PokemonData> GetPokemons()
        {
            return InventoryItems
                .Select(kvp => kvp.Value.InventoryItemData?.PokemonData)
                .Where(p => p != null && p.PokemonId > 0);
        }

        public PokemonData GetPokemon(ulong pokemonId)
        {
            return GetPokemons().FirstOrDefault(p => p.Id == pokemonId);
        }
        
        public async Task<ReleasePokemonResponse> TransferPokemon(ulong pokemonId)
        {
            if (GetPokemon(pokemonId) == null)
                return new ReleasePokemonResponse() { Result = ReleasePokemonResponse.Types.Result.Success };

            var transferPokemonRequest = new Request
            {
                RequestType = RequestType.ReleasePokemon,
                RequestMessage = ((IMessage)new ReleasePokemonMessage
                {
                    PokemonId = pokemonId
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(transferPokemonRequest, Client)).ConfigureAwait(false);

            Tuple<ReleasePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, ReleasePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            ReleasePokemonResponse releaseResponse = response.Item1;
            if (releaseResponse.Result == ReleasePokemonResponse.Types.Result.Success)
            {
                RemoveInventoryItem(GetPokemonHashKey(pokemonId));
            }

            return releaseResponse;
        }

        public async Task<ReleasePokemonResponse> TransferPokemons(List<ulong> pokemonIds)
        {
            // Filter out all pokemons that don't exist and duplicates.
            pokemonIds = GetPokemons().Where(p => pokemonIds.Contains(p.Id)).Select(p => p.Id).Distinct().ToList();

            var message = new ReleasePokemonMessage();
            message.PokemonIds.AddRange(pokemonIds);

            var transferPokemonRequest = new Request
            {
                RequestType = RequestType.ReleasePokemon,
                RequestMessage = message.ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(transferPokemonRequest, Client)).ConfigureAwait(false);

            Tuple<ReleasePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, ReleasePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            ReleasePokemonResponse releaseResponse = response.Item1;
            if (releaseResponse.Result == ReleasePokemonResponse.Types.Result.Success)
            {
                foreach (var pokemonId in pokemonIds)
                {
                    RemoveInventoryItem(GetPokemonHashKey(pokemonId));
                }
            }

            return releaseResponse;
        }

        public async Task<EvolvePokemonResponse> EvolvePokemon(ulong pokemonId, ItemId ievolutionItem = ItemId.ItemUnknown)
        {
            var evolvePokemonRequest = new Request
            {
                RequestType = RequestType.EvolvePokemon,
                RequestMessage = ((IMessage)new EvolvePokemonMessage
                {
                    PokemonId = pokemonId    ,
                    EvolutionItemRequirement = ievolutionItem

                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(evolvePokemonRequest, Client)).ConfigureAwait(false);

            Tuple<EvolvePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, EvolvePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            EvolvePokemonResponse evolveResponse = response.Item1;
            return evolveResponse;
        }

        public async Task<UpgradePokemonResponse> UpgradePokemon(ulong pokemonId)
        {
            var upgradePokemonRequest = new Request
            {
                RequestType = RequestType.UpgradePokemon,
                RequestMessage = ((IMessage)new UpgradePokemonMessage
                {
                    PokemonId = pokemonId
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(upgradePokemonRequest, Client)).ConfigureAwait(false);

            Tuple<UpgradePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, UpgradePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            UpgradePokemonResponse upgradePokemonResponse = response.Item1;
            return upgradePokemonResponse;
        }

        public async Task<GetInventoryResponse> GetInventory()
        {
            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.GetCommonRequests(Client)).ConfigureAwait(false);

            Tuple<CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item1;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item3;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item5;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return getInventoryResponse;
        }

        public async Task<RecycleInventoryItemResponse> RecycleItem(ItemId itemId, int amount)
        {
            var recycleItemRequest = new Request
            {
                RequestType = RequestType.RecycleInventoryItem,
                RequestMessage = ((IMessage)new RecycleInventoryItemMessage
                {
                    ItemId = itemId,
                    Count = amount
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(recycleItemRequest, Client)).ConfigureAwait(false);

            Tuple<RecycleInventoryItemResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, RecycleInventoryItemResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<UseItemXpBoostResponse> UseItemXpBoost()
        {
            var useItemXpBoostRequest = new Request
            {
                RequestType = RequestType.UseItemXpBoost,
                RequestMessage = ((IMessage)new UseItemXpBoostMessage
                {
                    ItemId = ItemId.ItemLuckyEgg
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(useItemXpBoostRequest, Client)).ConfigureAwait(false);

            Tuple<UseItemXpBoostResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, UseItemXpBoostResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<UseItemEggIncubatorResponse> UseItemEggIncubator(string itemId, ulong pokemonId)
        {
            var useItemEggIncubatorRequest = new Request
            {
                RequestType = RequestType.UseItemEggIncubator,
                RequestMessage = ((IMessage)new UseItemEggIncubatorMessage
                {
                    ItemId = itemId,
                    PokemonId = pokemonId
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(useItemEggIncubatorRequest, Client)).ConfigureAwait(false);

            Tuple<UseItemEggIncubatorResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, UseItemEggIncubatorResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<GetHatchedEggsResponse> GetHatchedEgg()
        {
            var getHatchedEggRequest = new Request
            {
                RequestType = RequestType.GetHatchedEggs,
                RequestMessage = ((IMessage)new GetHatchedEggsMessage()).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(getHatchedEggRequest, Client)).ConfigureAwait(false);

            Tuple<GetHatchedEggsResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, GetHatchedEggsResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<UseItemPotionResponse> UseItemPotion(ItemId itemId, ulong pokemonId)
        {
            var useItemPotionRequest = new Request
            {
                RequestType = RequestType.UseItemPotion,
                RequestMessage = ((IMessage)new UseItemPotionMessage
                {
                    ItemId = itemId,
                    PokemonId = pokemonId
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(useItemPotionRequest, Client)).ConfigureAwait(false);

            Tuple<UseItemPotionResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, UseItemPotionResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<UseItemReviveResponse> UseItemRevive(ItemId itemId, ulong pokemonId)
        {
            var useItemReviveRequest = new Request
            {
                RequestType = RequestType.UseItemRevive,
                RequestMessage = ((IMessage)new UseItemReviveMessage
                {
                    ItemId = itemId,
                    PokemonId = pokemonId
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(useItemReviveRequest, Client)).ConfigureAwait(false);

            Tuple<UseItemReviveResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, UseItemReviveResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<UseIncenseResponse> UseIncense(ItemId incenseType)
        {
            var useIncenseRequest = new Request
            {
                RequestType = RequestType.UseIncense,
                RequestMessage = ((IMessage)new UseIncenseMessage
                {
                    IncenseType = incenseType
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(useIncenseRequest, Client)).ConfigureAwait(false);

            Tuple<UseIncenseResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, UseIncenseResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<UseItemGymResponse> UseItemInGym(string gymId, ItemId itemId)
        {
            var useItemInGymRequest = new Request
            {
                RequestType = RequestType.UseItemGym,
                RequestMessage = ((IMessage)new UseItemGymMessage
                {
                    ItemId = itemId,
                    GymId = gymId,
                    PlayerLatitude = Client.CurrentLatitude,
                    PlayerLongitude = Client.CurrentLongitude
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(useItemInGymRequest, Client)).ConfigureAwait(false);

            Tuple<UseItemGymResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, UseItemGymResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<NicknamePokemonResponse> NicknamePokemon(ulong pokemonId, string nickName)
        {
            var nicknamePokemonRequest = new Request
            {
                RequestType = RequestType.NicknamePokemon,
                RequestMessage = ((IMessage)new NicknamePokemonMessage
                {
                    PokemonId = pokemonId,
                    Nickname = nickName
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(nicknamePokemonRequest, Client)).ConfigureAwait(false);

            Tuple<NicknamePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, NicknamePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }

        public async Task<SetFavoritePokemonResponse> SetFavoritePokemon(ulong pokemonId, bool isFavorite)
        {
            var setFavoritePokemonRequest = new Request
            {
                RequestType = RequestType.SetFavoritePokemon,
                RequestMessage = ((IMessage)new SetFavoritePokemonMessage
                {
                    PokemonId = (long)pokemonId,
                    IsFavorite = isFavorite
                }).ToByteString()
            };

            var request = await GetRequestBuilder().GetRequestEnvelope(CommonRequest.FillRequest(setFavoritePokemonRequest, Client)).ConfigureAwait(false);

            Tuple<SetFavoritePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse, CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse> response =
                await
                    PostProtoPayload
                        <Request, SetFavoritePokemonResponse, CheckChallengeResponse, GetHatchedEggsResponse, GetInventoryResponse,
                            CheckAwardedBadgesResponse, DownloadSettingsResponse, GetBuddyWalkedResponse>(request).ConfigureAwait(false);

            CheckChallengeResponse checkChallengeResponse = response.Item2;
            CommonRequest.ProcessCheckChallengeResponse(Client, checkChallengeResponse);

            GetInventoryResponse getInventoryResponse = response.Item4;
            CommonRequest.ProcessGetInventoryResponse(Client, getInventoryResponse);

            DownloadSettingsResponse downloadSettingsResponse = response.Item6;
            CommonRequest.ProcessDownloadSettingsResponse(Client, downloadSettingsResponse);

            return response.Item1;
        }
        public IEnumerable<ItemData> GetItemsData(){
            var items = InventoryItems.Values.Select(x => x.InventoryItemData.Item)
                .Where(x=> x!=null);
            return items;
        }

        public ItemData GetItemData( ItemId itemId)
        {
            return GetItemsData()?.FirstOrDefault(p => p.ItemId == itemId);
        }

        public int GetItemCount(ItemId itemId)
        {
            var itemData = GetItemData(itemId);
            return (itemData!=null)? itemData.Count:0;
        }

        public int GetItemsCount()
        {
            return InventoryItems.Values
                .Where(p => p.InventoryItemData.Item != null)
                .Sum(p => p.InventoryItemData.Item.Count);
        }

        public IEnumerable<PlayerStats> GetPlayerStats()
        {
            return InventoryItems.Values.Select(i => i.InventoryItemData?.PlayerStats)
                .Where(i => i != null);
        }

        public IEnumerable<EggIncubator> GetIncubators()
        {
            return InventoryItems.Values.Where(x => x.InventoryItemData.EggIncubators != null)
                    .SelectMany(i => i.InventoryItemData.EggIncubators.EggIncubator)
                    .Where(i => i != null);
        }

        public IEnumerable<PokemonData> GetEggs()
        {
            return  InventoryItems.Values.Select(i => i.InventoryItemData?.PokemonData)
               .Where(p => p != null && p.IsEgg);
        }
    }
}