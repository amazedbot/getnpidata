# us-counties.topo.json

The county and state shapes of the area maps (docs/PRODUCT_PLAN.md step 3.3), drawn with D3 by `js/area-map.js`.

- **Source:** US Census Bureau, 2025 cartographic boundary file, counties, 1:20,000,000
  (`https://www2.census.gov/geo/tiger/GENZ2025/shp/cb_2025_us_county_20m.zip`, 907,162 bytes, SHA-256
  `745a193ddcb63337befa83a55c031c0fdd0c16a66fd12b56e2f274829624bb65`). Public domain (US government work).
- **Why this vintage:** current boundaries, the ones HUD's ZIP–county crosswalk and the Census Gazetteer use: Connecticut's
  nine planning regions (09110–09190) instead of its eight old counties, Alaska's 2019 Chugach and Copper River areas.
  3,222 counties: the 50 states, DC and Puerto Rico. Guam, the Virgin Islands, American Samoa and the Northern Mariana
  Islands are not in the file (their areas are listed in the pages' tables).
- **Contents:** two objects, `counties` (id = 5-digit county FIPS) and `states` (id = postal code, dissolved from the
  counties), no other properties. Already projected (AlbersUSA with Alaska, Hawaii and Puerto Rico as insets), so the page
  draws it with `d3.geoIdentity().reflectY(true)`; 18 small Aleutian island paths west of the 180th meridian are dropped by
  the projection.
- **Built with** [mapshaper](https://github.com/mbloch/mapshaper) 0.6.102 (not a project dependency), from the unzipped file:

  ```
  mapshaper cb_2025_us_county_20m.shp -proj albersusa +PR -simplify 12% weighted keep-shapes \
    -filter-fields GEOID,STUSPS -rename-layers counties -dissolve STUSPS + name=states \
    -filter-fields STUSPS target=states -filter-fields GEOID target=counties \
    -o us-counties.topo.json format=topojson target=counties,states id-field=GEOID,STUSPS drop-table quantization=20000
  ```

  511 KB (164 KB gzipped). A newer vintage: download the newest `GENZ<year>/shp/cb_<year>_us_county_20m.zip` and run the
  same command; check that the county FIPS still match the `county` table (the Gazetteer).
