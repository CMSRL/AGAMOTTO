import socket
import os
import sys

from XMLHelpers import parse_cot

HOST = '127.0.0.1'  # Localhost
PORT = 65432        # Port to listen on

def receive_xml(conn):
    xml_data = conn.recv(1024).decode('utf-8')  # Receive up to 1024 bytes
    with open("received.xml", "w", encoding="utf-8") as f:
        f.write(xml_data)
    print("XML file received and saved as 'received.xml'.")

def send_xml(conn, filepath):
    if not os.path.exists(filepath):
        print(f"File {filepath} not found.")
        return
    with open(filepath, "r", encoding="utf-8") as f:
        xml_content = f.read()
        conn.sendall(xml_content.encode('utf-8'))
    print(f"XML file '{filepath}' sent.")

def try_link_route_to_entity(route, entities):
    """Match a route to an entity if the first route point matches an entity's lat/lon up to 5 decimal places """
    verts = route.get("verticies") or []
    if not verts:
        return None

    first = verts[0]
    r_lat = f"{first.get('lat'):.5f}" if first.get('lat') is not None else None
    r_lon = f"{first.get('lon'):.5f}" if first.get('lon') is not None else None
    if r_lat is None or r_lon is None:
        return None

    for ent in entities.values():
        e_lat = ent.get("lat"); e_lon = ent.get("lon")
        if e_lat is None or e_lon is None:
            continue

        e_lat_str = f"{float(e_lat):.5f}"
        e_lon_str = f"{float(e_lon):.5f}"

        if e_lat_str == r_lat and e_lon_str == r_lon:
            return ent["uid"]

    return None

def start_server():
    entities = {} # cache of last seen entities (uid) so we can link to routes

    while True:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.bind((HOST, PORT))
            s.listen()
            print(f"Server listening on {HOST}:{PORT}...")

            conn, addr = s.accept()
            with conn:
                print(f"Connected by {addr}")
                while True:
                    size_bytes = conn.recv(4)
                    if not size_bytes:
                        break
                    size_number = int.from_bytes(size_bytes, byteorder='little')
                    data = conn.recv(size_number).decode('utf-8')
                    if not data:
                        break

                    # parse + print
                    parsed = parse_cot(data)

                    # route event
                    if parsed and parsed.get('__normalized_type__') == 'route':
                        r = parsed['route']
                        name = r.get('name') or parsed.get('callsign') or '(unnamed)'
                        verts = r.get('verticies', [])
                        print(f"[ROUTE] name={name} vertices={len(verts)}")
                        
                        for i, v in enumerate(verts[:5]):
                            lat = v.get('lat'); lon = v.get('lon'); alt = v.get('alt')
                            if alt is not None:
                                print(f"  {i:02d}: lat={lat:.6f} lon={lon:.6f} alt={alt:.1f}")
                            else:
                                print(f"  {i:02d}: lat={lat:.6f} lon={lon:.6f}")
                        if len(verts) > 5:
                            print(f"  ... ({len(verts)-5} more)")

                        linked_uid = try_link_route_to_entity(r, entities)
                        if linked_uid:
                            print(f"  linked-to entity uid={linked_uid}")
                        continue

                    if parsed:
                        print(parsed)
                           
                        # update entity cache if payload has location + uid
                        uid = parsed.get("uid")
                        lat = parsed.get("lat")
                        lon = parsed.get("lon")
                        if uid is not None and lat is not None and lon is not None:
                            entities[uid] = {
                                "uid": uid,
                                "lat": lat,
                                "lon": lon,
                                "callsign": parsed.get("callsign")
                            }
                    else:
                        print("[WARN] could not parse payload")

                    continue

if __name__ == "__main__":
    start_server()