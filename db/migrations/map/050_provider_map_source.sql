-- 050_provider_map_source.sql
--
-- How each map point was placed (CLAUDE.md §7 Stage 5.5 item 10): address, place or place_name (Overture, at the
-- building), census (Census geocoder, interpolated along the street) or zip (the ZIP centroid; approximate = 1).
-- Added at the end with a default, so the change is instant; MapBuilder fills it on the next rebuild.

ALTER TABLE `provider_map` ADD COLUMN `source` VARCHAR(12) NOT NULL DEFAULT 'zip';
