import socket
import json

PLUGIN_HOST = "127.0.0.1"
PLUGIN_PORT = 65433

def send(payload: str):
    data = payload.encode("utf-8")
    header = len(data).to_bytes(4, "little")
    with socket.create_connection((PLUGIN_HOST, PLUGIN_PORT), timeout=5) as s:
        s.sendall(header + data)

if __name__ == "__main__":
    # Send text & JSON
    send("route test_route -> score=0.84, avg speed=1.9 m/s, exposures=3")

    # we can use some format like this to parse later in winTAK
    msg = {
        "kind": "assessment",
        "routeUid": "7c213d05-a410-4428-86db-479e92e68949",
        "entityUid": "a3de7e29-c75d-4be0-bbaf-2df281a6a16b",
        "score": 0.84,
        "notes": "notes"
    }
    send(json.dumps(msg))
