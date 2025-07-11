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

    # Callsign
    contact = root.find('detail/contact')
    if contact is not None:
        data['callsign'] = contact.attrib.get('callsign')

    return data