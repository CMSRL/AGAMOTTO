import socket
import os

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

def start_server():
    while True:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.bind((HOST, PORT))
            s.listen()
            print(f"Server listening on {HOST}:{PORT}...")

            conn, addr = s.accept()
            with conn:
                print(f"Connected by {addr}")
                while True:
                    data = conn.recv(1024).decode('utf-8')
                    if not data:
                        break
                    else:
                        xmlCoT = parse_cot(data)
                        print(xmlCoT)
                        #print(xmlCoT.get('uid'))
                        break

if __name__ == "__main__":
    start_server()
