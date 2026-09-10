"""Convert JMA AreaForecastLocalE shapefiles to the offline review map.
Requires pyshp 3.1.6 and shapely 2.1.2; not runtime dependencies.
Usage: python scripts/generate-trial-seismic-map.py <shapefile-directory> <output-json>
"""
import json
import pathlib
import sys
import shapefile
from shapely.geometry import shape
from shapely.ops import unary_union

reader = shapefile.Reader(str(next(pathlib.Path(sys.argv[1]).rglob('*.shp'))), encoding='utf-8')
regions = []
grouped = {}
for item in reader.iterShapeRecords():
    attributes = item.record.as_dict()
    geometry = shape(item.shape.__geo_interface__)
    if not geometry.is_valid:
        raise ValueError('Invalid source geometry: ' + attributes['code'])
    grouped.setdefault(attributes['code'], (attributes, []))[1].append(geometry)
for attributes, shapes in grouped.values():
    geometry = unary_union(shapes).simplify(0.003, preserve_topology=True)
    center = geometry.representative_point()
    polygons = [geometry] if geometry.geom_type == 'Polygon' else list(geometry.geoms)
    rings = []
    for polygon in polygons:
        rings.extend([list(polygon.exterior.coords)] + [list(r.coords) for r in polygon.interiors])
    regions.append(dict(code=attributes['code'], name=attributes['name'],
                        center=[center.x, center.y], polygons=rings))
assert len(regions) == len({r['code'] for r in regions})
output = dict(source='https://www.data.jma.go.jp/developer/gis/20240520_AreaForecastLocalE_GIS.zip',
              note='JMA GIS (JGD2011), simplified 0.003 degrees by CDI-Telopper; not an estimated intensity surface',
              regions=regions)
pathlib.Path(sys.argv[2]).write_text(json.dumps(output, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
print('Regions:', len(regions))
