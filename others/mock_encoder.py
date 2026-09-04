import time
import math
import asyncio
from typing import Optional, Dict, Any
from fastapi import FastAPI, Response, Request, HTTPException
from fastapi.responses import HTMLResponse, JSONResponse
from pydantic import BaseModel
import uvicorn

app = FastAPI(title="Mock Encoder Server", version="1.0.0")

# --------------------------------------------------------------------------- #
# State Engine
# --------------------------------------------------------------------------- #

class EncoderState:
    def __init__(self):
        self.start_time: float = time.time()
        self.last_update_time: float = self.start_time
        
        # Core telemetry
        self.mm: float = 0.0
        self.speed: float = 0.0  # mm/s
        self.seq: int = 0
        self.zero_count: int = 0
        self.zero_mm: float = 0.0
        self.button_momentary: int = 0
        self.dir: int = 1
        
        # Paused state (Line stopped)
        self.is_paused: bool = False
        
        # Fault injection
        self.fault_mode: str = "none"  # "none", "hang", "refuse", "freeze-seq", "freeze-mm"
        self.fault_until: Optional[float] = None
        self.latency_ms: int = 0
        
        # Internal clock state
        self._lock = asyncio.Lock()

    def update_position(self):
        """Advance mm based on wall-clock time elapsed and current speed."""
        now = time.time()
        dt = now - self.last_update_time
        self.last_update_time = now
        
        # Clear fault if duration expired
        if self.fault_until and now >= self.fault_until:
            self.fault_mode = "none"
            self.fault_until = None

        # Do not advance position if paused or in freeze-mm fault mode
        if not self.is_paused and self.fault_mode != "freeze-mm":
            self.mm += self.speed * dt

    def get_telemetry(self) -> Dict[str, Any]:
        """Generate one snapshot conforming strictly to the GET /encoder spec."""
        self.update_position()
        
        now = time.time()
        uptime_ms = int((now - self.start_time) * 1000)
        
        # Prepare response
        btn = self.button_momentary
        self.button_momentary = 0  # Clear momentary button press flag after reading
        
        return {
            "seq": self.seq,
            "uptimeMs": uptime_ms,
            "mm": round(self.mm, 4),
            "zeroCount": self.zero_count,
            "zeroMm": round(self.zero_mm, 4),
            "button": btn,
            "dir": self.dir
        }

state = EncoderState()

# --------------------------------------------------------------------------- #
# Background Clock (~50 Hz Sequence Generator)
# --------------------------------------------------------------------------- #

async def sequence_clock_loop():
    """Independent ~50Hz clock loop to advance sequence count."""
    while True:
        await asyncio.sleep(0.02)  # ~50 Hz (20 ms interval)
        if state.fault_mode != "freeze-seq":
            state.seq += 1

@app.on_event("startup")
async def startup_event():
    asyncio.create_task(sequence_clock_loop())
    print("\n" + "=" * 70)
    print("🚀 Mock Encoder PCB Server Running!")
    print("👉 GET /encoder Endpoint : http://localhost:5055/encoder")
    print("👉 Web UI Control Panel  : http://localhost:5055/ui")
    print("=" * 70 + "\n")

# --------------------------------------------------------------------------- #
# Public Contract Endpoint (GET /encoder)
# --------------------------------------------------------------------------- #

@app.get("/encoder")
async def get_encoder():
    # Handle fault modes
    if state.fault_mode == "refuse":
        # Simulate connection reset/refusal
        raise HTTPException(status_code=503, detail="Connection refused by simulated PCB hardware")
    
    if state.fault_mode == "hang":
        # Hang connection indefinitely or for duration
        while state.fault_mode == "hang":
            if state.fault_until and time.time() >= state.fault_until:
                state.fault_mode = "none"
                state.fault_until = None
                break
            await asyncio.sleep(0.1)

    # Apply artificial latency if configured
    if state.latency_ms > 0:
        await asyncio.sleep(state.latency_ms / 1000.0)

    data = state.get_telemetry()
    print(f"[POLL] GET /encoder -> mm: {data['mm']} | seq: {data['seq']} | zeroMm: {data['zeroMm']}")
    return JSONResponse(content=data)

# --------------------------------------------------------------------------- #
# Control API (Pydantic Models & Endpoints)
# --------------------------------------------------------------------------- #

class SpeedModel(BaseModel):
    mmPerSec: float

class JumpModel(BaseModel):
    mm: float

class FaultModel(BaseModel):
    mode: str  # "none", "hang", "refuse", "freeze-seq", "freeze-mm"
    durationMs: Optional[int] = None

class LatencyModel(BaseModel):
    delayMs: int

@app.post("/control/speed")
async def set_speed(body: SpeedModel):
    state.update_position()
    state.speed = body.mmPerSec
    state.dir = -1 if body.mmPerSec < 0 else 1
    print(f"[CONTROL] Speed set to {state.speed} mm/s (Direction: {state.dir})")
    return {"status": "ok", "speed": state.speed, "dir": state.dir}

@app.post("/control/press")
async def button_press():
    state.update_position()
    state.zero_count += 1
    state.zero_mm = state.mm
    state.button_momentary = 1
    print(f"[CONTROL] Button pressed! zeroCount: {state.zero_count}, zeroMm: {state.zero_mm}")
    return {"status": "ok", "zeroCount": state.zero_count, "zeroMm": state.zero_mm}

@app.post("/control/pause")
async def pause_line():
    state.update_position()
    state.is_paused = True
    print("[CONTROL] Line PAUSED (mm frozen, seq advancing)")
    return {"status": "ok", "is_paused": True}

@app.post("/control/resume")
async def resume_line():
    state.update_position()
    state.is_paused = False
    print("[CONTROL] Line RESUMED")
    return {"status": "ok", "is_paused": False}

@app.post("/control/jump")
async def jump_position(body: JumpModel):
    state.update_position()
    state.mm = body.mm
    print(f"[CONTROL] Position jumped to {state.mm} mm")
    return {"status": "ok", "mm": state.mm}

@app.post("/control/fault")
async def inject_fault(body: FaultModel):
    state.update_position()
    state.fault_mode = body.mode
    if body.durationMs and body.durationMs > 0:
        state.fault_until = time.time() + (body.durationMs / 1000.0)
    else:
        state.fault_until = None
    print(f"[CONTROL] Fault set to '{body.mode}' (Duration: {body.durationMs} ms)")
    return {"status": "ok", "fault_mode": state.fault_mode, "durationMs": body.durationMs}

@app.post("/control/latency")
async def set_latency(body: LatencyModel):
    state.latency_ms = max(0, body.delayMs)
    print(f"[CONTROL] Fixed latency set to {state.latency_ms} ms")
    return {"status": "ok", "latencyMs": state.latency_ms}

@app.get("/control/status")
async def get_status():
    state.update_position()
    return {
        "speed": state.speed,
        "mm": round(state.mm, 4),
        "zeroCount": state.zero_count,
        "zeroMm": round(state.zero_mm, 4),
        "isPaused": state.is_paused,
        "faultMode": state.fault_mode,
        "latencyMs": state.latency_ms,
        "seq": state.seq,
        "dir": state.dir
    }

# --------------------------------------------------------------------------- #
# Web Control UI Dashboard (Dark Themed)
# --------------------------------------------------------------------------- #

@app.get("/ui", response_class=HTMLResponse)
async def serve_ui():
    return """
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">
        <title>Mock Encoder Controller & Dashboard</title>
        <style>
            body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; background-color: #121212; color: #e0e0e0; margin: 0; padding: 20px; }
            .container { max-width: 1000px; margin: 0 auto; }
            h1 { color: #00e676; border-bottom: 2px solid #333; padding-bottom: 10px; }
            .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 20px; margin-top: 20px; }
            .card { background-color: #1e1e1e; border-radius: 8px; padding: 20px; box-shadow: 0 4px 6px rgba(0,0,0,0.3); border: 1px solid #333; }
            .card h2 { margin-top: 0; font-size: 1.2rem; color: #80d8ff; border-bottom: 1px solid #444; padding-bottom: 8px; }
            .stat-value { font-size: 1.8rem; font-weight: bold; color: #ffffff; margin: 5px 0; font-family: monospace; }
            .stat-label { font-size: 0.85rem; color: #a0a0a0; text-transform: uppercase; }
            button { background-color: #2979ff; color: white; border: none; padding: 10px 16px; border-radius: 4px; font-weight: bold; cursor: pointer; margin-top: 8px; transition: 0.2s; }
            button:hover { background-color: #5393ff; }
            button.danger { background-color: #ff1744; }
            button.danger:hover { background-color: #ff616f; }
            button.warning { background-color: #ff9100; color: #000; }
            button.warning:hover { background-color: #ffab40; }
            input { background-color: #2c2c2c; border: 1px solid #444; color: white; padding: 8px; border-radius: 4px; width: 80%; margin-top: 5px; }
            .status-badge { display: inline-block; padding: 4px 8px; border-radius: 4px; font-weight: bold; font-size: 0.85rem; }
            .badge-green { background-color: #1b5e20; color: #a5d6a7; }
            .badge-red { background-color: #b71c1c; color: #ffcdd2; }
            .badge-orange { background-color: #e65100; color: #ffe0b2; }
            .btn-group { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 10px; }
        </style>
    </head>
    <body>
        <div class="container">
            <h1>⚙️ Mock Encoder Control Dashboard</h1>
            
            <div class="grid">
                <!-- Live Telemetry Card -->
                <div class="card" style="grid-column: span 2;">
                    <h2>Live Hardware Telemetry (GET /encoder)</h2>
                    <div style="display: flex; justify-content: space-between; flex-wrap: wrap;">
                        <div>
                            <div class="stat-label">Absolute Travel (mm)</div>
                            <div class="stat-value" id="val-mm">0.00</div>
                        </div>
                        <div>
                            <div class="stat-label">Relative Travel (mm - zeroMm)</div>
                            <div class="stat-value" id="val-rel-mm" style="color:#00e676;">0.00</div>
                        </div>
                        <div>
                            <div class="stat-label">Speed (mm/s)</div>
                            <div class="stat-value" id="val-speed">0.0</div>
                        </div>
                        <div>
                            <div class="stat-label">Sequence (seq)</div>
                            <div class="stat-value" id="val-seq">0</div>
                        </div>
                    </div>
                    <div style="margin-top: 15px; display: flex; gap: 20px; font-size: 0.9rem;">
                        <div>Zero Count: <b id="val-zerocount">0</b></div>
                        <div>Zero mm: <b id="val-zeromm">0.0</b></div>
                        <div>Direction: <b id="val-dir">1</b></div>
                        <div>Fault State: <span id="val-fault" class="status-badge badge-green">NONE</span></div>
                    </div>
                </div>

                <!-- Speed & Line Control -->
                <div class="card">
                    <h2>Line Control</h2>
                    <div>
                        <label class="stat-label">Set Speed (mm/s)</label><br>
                        <input type="number" id="inp-speed" value="166.7" step="10">
                        <button onclick="setSpeed()">Apply Speed</button>
                    </div>
                    <div class="btn-group" style="margin-top: 15px;">
                        <button class="warning" onclick="togglePause()" id="btn-pause">Pause Line</button>
                        <button class="warning" onclick="triggerPress()">🔘 Simulate Button Press</button>
                    </div>
                </div>

                <!-- Teleport / Position Jump -->
                <div class="card">
                    <h2>Position Jump (Teleport)</h2>
                    <label class="stat-label">Set Absolute Position (mm)</label><br>
                    <input type="number" id="inp-jump" value="5000000" step="1000">
                    <button onclick="jump()">Teleport mm</button>
                    <div class="btn-group">
                        <button onclick="quickJump(1000000)">1 km</button>
                        <button onclick="quickJump(5000000)">5 km (Precision Test)</button>
                    </div>
                </div>

                <!-- Fault Injection -->
                <div class="card">
                    <h2>Fault Injection & Latency</h2>
                    <div class="btn-group">
                        <button class="danger" onclick="setFault('hang')">Hang (Timeout)</button>
                        <button class="danger" onclick="setFault('refuse')">Refuse Connection</button>
                        <button class="warning" onclick="setFault('freeze-seq')">Freeze Sequence</button>
                        <button class="warning" onclick="setFault('freeze-mm')">Freeze Movement</button>
                        <button onclick="setFault('none')">Clear Faults</button>
                    </div>
                    <div style="margin-top: 15px;">
                        <label class="stat-label">Artificial Latency (ms)</label><br>
                        <input type="number" id="inp-latency" value="200">
                        <button onclick="setLatency()">Set Latency</button>
                        <button onclick="clearLatency()">Clear Latency</button>
                    </div>
                </div>
            </div>
        </div>

        <script>
            async function fetchStatus() {
                try {
                    const res = await fetch('/control/status');
                    const data = await res.json();
                    
                    document.getElementById('val-mm').innerText = data.mm.toFixed(2);
                    document.getElementById('val-rel-mm').innerText = (data.mm - data.zeroMm).toFixed(2);
                    document.getElementById('val-speed').innerText = data.speed.toFixed(1);
                    document.getElementById('val-seq').innerText = data.seq;
                    document.getElementById('val-zerocount').innerText = data.zeroCount;
                    document.getElementById('val-zeromm').innerText = data.zeroMm.toFixed(2);
                    document.getElementById('val-dir').innerText = data.dir;
                    
                    const btnPause = document.getElementById('btn-pause');
                    if(data.isPaused) {
                        btnPause.innerText = "Resume Line";
                        btnPause.className = "button";
                    } else {
                        btnPause.innerText = "Pause Line";
                        btnPause.className = "button warning";
                    }

                    const faultEl = document.getElementById('val-fault');
                    faultEl.innerText = data.faultMode.toUpperCase();
                    if(data.faultMode === 'none') {
                        faultEl.className = "status-badge badge-green";
                    } else {
                        faultEl.className = "status-badge badge-red";
                    }
                } catch (e) {
                    console.error("Error fetching status", e);
                }
            }

            async function postData(url, data = {}) {
                await fetch(url, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(data)
                });
                fetchStatus();
            }

            function setSpeed() {
                const spd = parseFloat(document.getElementById('inp-speed').value);
                postData('/control/speed', { mmPerSec: spd });
            }

            function togglePause() {
                const btn = document.getElementById('btn-pause');
                if (btn.innerText.includes("Pause")) {
                    postData('/control/pause');
                } else {
                    postData('/control/resume');
                }
            }

            function triggerPress() {
                postData('/control/press');
            }

            function jump() {
                const mm = parseFloat(document.getElementById('inp-jump').value);
                postData('/control/jump', { mm: mm });
            }

            function quickJump(mm) {
                postData('/control/jump', { mm: mm });
            }

            function setFault(mode) {
                postData('/control/fault', { mode: mode });
            }

            function setLatency() {
                const ms = parseInt(document.getElementById('inp-latency').value);
                postData('/control/latency', { delayMs: ms });
            }

            function clearLatency() {
                postData('/control/latency', { delayMs: 0 });
            }

            setInterval(fetchStatus, 200);
            fetchStatus();
        </script>
    </body>
    </html>
    """

# --------------------------------------------------------------------------- #
# Main Entrypoint
# --------------------------------------------------------------------------- #

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=5055, log_level="warning")