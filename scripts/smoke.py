"""End-to-end smoke test against a running bridge.

    python scripts/smoke.py

Discovers the newest live session, then exercises every command.
"""

import base64
import json
import os
import socket
import struct
import sys
import time

HEADER = struct.Struct(">I")


def discovery_directory():
    base = os.environ.get("LOCALAPPDATA") or os.path.join(
        os.path.expanduser("~"), ".local", "state"
    )
    return os.path.join(base, "DotCraft", "blender-bridge")


def sessions():
    directory = discovery_directory()
    if not os.path.isdir(directory):
        return []
    found = []
    for name in sorted(os.listdir(directory)):
        if name.startswith("bridge-") and name.endswith(".json"):
            with open(os.path.join(directory, name), encoding="utf-8") as handle:
                found.append(json.load(handle))
    return sorted(found, key=lambda s: s["startedAt"], reverse=True)


class Client:
    def __init__(self, descriptor):
        self.counter = 0
        self.sock = socket.create_connection(("127.0.0.1", descriptor["port"]), timeout=30)
        self.sock.settimeout(None)
        hello = self.call("hello", token=descriptor["token"])
        if not hello["ok"]:
            raise SystemExit("handshake failed: %s" % hello["error"])
        self.identity = hello["result"]

    def call(self, kind, **params):
        self.counter += 1
        token = params.pop("token", None)
        payload = {"id": str(self.counter), "type": kind, "params": params}
        if token is not None:
            payload["token"] = token
        body = json.dumps(payload).encode("utf-8")
        self.sock.sendall(HEADER.pack(len(body)) + body)
        (length,) = HEADER.unpack(self._read(HEADER.size))
        return json.loads(self._read(length).decode("utf-8"))

    def _read(self, count):
        chunks, remaining = [], count
        while remaining:
            chunk = self.sock.recv(remaining)
            if not chunk:
                raise SystemExit("connection closed")
            chunks.append(chunk)
            remaining -= len(chunk)
        return b"".join(chunks)


def show(label, response, *, keys=None):
    status = "ok " if response["ok"] else "ERR"
    if not response["ok"]:
        message = response["error"]["message"].strip().splitlines()[-1]
        print("  %s %-14s %s: %s" % (status, label, response["error"]["code"], message))
        return response
    result = response.get("result")
    if keys and isinstance(result, dict):
        result = {k: result[k] for k in keys if k in result}
    rendered = json.dumps(result, default=str)
    print("  %s %-14s %s" % (status, label, rendered[:200]))
    return response


def main():
    found = sessions()
    if not found:
        raise SystemExit("no live bridge; start Blender with bridge/bootstrap.py first")
    descriptor = found[0]
    print("session pid=%(pid)s port=%(port)s blender=%(blenderVersion)s" % descriptor)

    started = time.time()
    client = Client(descriptor)
    print("  handshake %.0f ms" % ((time.time() - started) * 1000))

    show("ping", client.call("ping"))
    show("status", client.call("status"), keys=["scene", "mode", "engine", "hasViewport", "counts"])
    show("documentation", {"ok": True, "result": {"chars": len(client.call("documentation")["result"]["text"])}})

    code = """
import bpy
for i in range(3):
    bpy.ops.mesh.primitive_monkey_add(size=1.2, location=(i * 2.5, 0, 0))
    ob = bpy.context.active_object
    ob.name = "Smoke_%d" % i
    mat = bpy.data.materials.new("SmokeMat_%d" % i)
    mat.use_nodes = True
    mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (i / 3.0, 0.3, 0.9, 1.0)
    ob.data.materials.append(mat)
dc.result({"created": dc.selected(), "total": len(bpy.data.objects)})
print("three suzannes are in the scene")
"""
    show("execute", client.call("execute", code=code))
    show("execute/api", client.call("execute", code='dc.result(dc.api("bpy.ops.mesh.primitive_cube_add")["parameters"][:3])'))
    show("execute/search", client.call("execute", code='dc.result(dc.search("subdivide")[:5])'))
    show("execute/error", client.call("execute", code="raise ValueError('deliberate')"))
    show("scene", client.call("scene"), keys=["name", "engine", "resolution"])
    show("object", client.call("object", name="Smoke_1"), keys=["name", "type", "mesh", "materials"])

    view = client.call("view", maxSize=640)
    if view["ok"]:
        raw = base64.b64decode(view["result"]["base64"])
        print("  ok  %-14s %dx%d, %d KB png" % ("view", view["result"]["width"], view["result"]["height"], len(raw) // 1024))
        out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".out")
        os.makedirs(out, exist_ok=True)
        with open(os.path.join(out, "smoke-view.png"), "wb") as handle:
            handle.write(raw)
    else:
        show("view", view)

    started = time.time()
    job = show("render", client.call("render", resolution=[480, 270], engine="BLENDER_EEVEE"), keys=["jobId", "state", "detail"])
    if job["ok"]:
        job_id = job["result"]["jobId"]
        print("  .. render returned in %.0f ms (non-blocking if 'running')" % ((time.time() - started) * 1000))
        for _ in range(120):
            snapshot = client.call("job", jobId=job_id)["result"]
            if snapshot["state"] != "running":
                print("  ok  %-14s %s in %.1fs -> %s" % ("job", snapshot["state"], snapshot["elapsedSeconds"], json.dumps(snapshot["result"], default=str)[:80]))
                break
            time.sleep(0.25)
        else:
            print("  ERR render did not finish in 30s")

    show("engine/bad", client.call("render", engine="BLENDER_EEVEE_NEXT"))
    print("done")


if __name__ == "__main__":
    sys.exit(main())
