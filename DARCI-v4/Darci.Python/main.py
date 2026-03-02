"""
DARCI Python Service
====================
New service that runs alongside the .NET API.
Hosts the CAD generation engine (and future Python-based capabilities).

Start with:
    cd DARCI-v3/Darci.Python
    pip install -r requirements.txt
    uvicorn main:app --host 0.0.0.0 --port 8000
"""

from fastapi import FastAPI
from cad.cad_routes import router as cad_router
from cadcoder.cadcoder_routes import router as cadcoder_router
from simulation.simulation_routes import router as simulation_router

app = FastAPI(
    title="DARCI Python Service",
    version="1.0.0",
    description="CAD generation engine for DARCI v3"
)

app.include_router(cad_router)
app.include_router(cadcoder_router)
app.include_router(simulation_router)


@app.get("/")
async def health():
    return {"status": "alive", "service": "darci-python"}
