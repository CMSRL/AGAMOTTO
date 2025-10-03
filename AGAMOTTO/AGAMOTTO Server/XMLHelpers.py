import xml.etree.ElementTree as ET

def parse_cot(xml_string):
    root = ET.fromstring(xml_string)

    data = {}

    # Event attributes
    if root.tag == 'event':
        data['uid'] = root.attrib.get('uid')
        data['type'] = root.attrib.get('type')
        data['time'] = root.attrib.get('time')
        data['start'] = root.attrib.get('start')
        data['stale'] = root.attrib.get('stale')
        data['how'] = root.attrib.get('how')

    # Location info
    point = root.find('point')
    if point is not None:
        data['lat'] = point.attrib.get('lat')
        data['lon'] = point.attrib.get('lon')
        data['hae'] = point.attrib.get('hae')
        data['ce'] = point.attrib.get('ce')
        data['le'] = point.attrib.get('le')

    detail = root.find('detail')
    if detail is not None:
        # Callsign
        contact = detail.find('contact')
        if contact is not None:
            data['callsign'] = contact.attrib.get('callsign')

        # Links
        links = []
        for link in detail.findall('link'):
            links.append({
                'uid': link.attrib.get('uid'),
                'relation': link.attrib.get('relation')
            })
        if links:
            data['links'] = links

        # Waypoint order
        wp = detail.find('wp')
        if wp is not None and 'seq' in wp.attrib:
            try:
                data['wp_seq'] = int(wp.attrib['seq'])
            except ValueError:
                pass

        # Route info
        route = detail.find('route')
        if route is not None:
            name = route.attrib.get('name') or data.get('callsign') or data.get('uid')
            verts = []
            for p in route.findall('point'):
                lat = p.attrib.get('lat')
                lon = p.attrib.get('lon')
                hae = p.attrib.get('hae')
                if lat and lon:
                    try:
                        v = {'lat': float(lat), 'lon': float(lon)}
                        if hae is not None:
                            v['alt'] = float(hae)
                        verts.append(v)
                    except ValueError:
                        continue
            data['__normalized_type__'] = 'route'
            data['route'] = {'name': name, 'verticies': verts}

    return data
