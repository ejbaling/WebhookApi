# Document-level tags for RagDocument

This file documents the document-level tags added to `RagDocument.Tags`.

## Purpose

- Classify whole listings (house rules, policies, amenities) for filtering and routing.

## Primary tags for this listing

- `house_rules`
- `occupancy_policy`
- `children_policy`
- `no_pets`
- `quiet_hours`
- `kitchen_rules`
- `checkin_checkout`
- `listing_redwood_iloilo`

## Optional / additional tags

- `location_info`
- `amenities`
- `security_policy`

## Notes

- Only the `Tags` field was added to `RagDocument` (no changes to `RagChunk`).
- Implementation: `public string[] Tags { get; set; } = Array.Empty<string>();` (stored as `text[]` in Postgres when using EF Core + Npgsql).
- If you later need chunk-level filtering, consider adding a typed `Tags` field to `RagChunk` or use `MetadataJson`.
