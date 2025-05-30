﻿using vv.Domain.Models;
using vv.Infrastructure.Serialization;

namespace vv.Infrastructure.Mapping
{
    public static class BaseMarketDataMapper
    {
        public static BaseMarketDataDto ToDto(BaseMarketData domain)
        {
            ArgumentNullException.ThrowIfNull(domain);

            return new BaseMarketDataDto(domain.Id, domain.SchemaVersion, domain.Version,
                domain.AssetId, domain.AssetClass, domain.DataType, domain.Region, domain.DocumentType,
                domain.CreateTimestamp, domain.AsOfDate, domain.AsOfTime, domain.Tags.ToList() ?? new List<string>());
        }

        public static void ApplyToDomain(BaseMarketDataDto dto, BaseMarketData domain)
        {
            ArgumentNullException.ThrowIfNull(dto);
            ArgumentNullException.ThrowIfNull(domain);

            domain.SchemaVersion = dto.SchemaVersion;
            domain.Version = dto.Version;
            domain.AssetId = dto.AssetId;
            domain.AssetClass = dto.AssetClass;
            domain.DataType = dto.DataType;
            domain.Region = dto.Region;
            domain.DocumentType = dto.DocumentType;
            domain.AsOfDate = dto.AsOfDate;
            domain.AsOfTime = dto.AsOfTime;
            domain.CreateTimestamp = dto.CreateTimestamp ?? DateTime.UtcNow;

            // Add tags using the appropriate method
            if (dto.Tags != null)
            {
                foreach (var tag in dto.Tags)
                {
                    domain.AddTag(tag);
                }
            }
        }
    }
}