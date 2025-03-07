import requests
import json
import time
from datetime import datetime
import subprocess
import os

# CNN API URL
CNN_FEAR_GREED_URL = "https://production.dataviz.cnn.io/index/fearandgreed/graphdata/"

# Fake browser headers to bypass bot detection
HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36",
    "Accept-Language": "en-US,en;q=0.9",
    "Referer": "https://edition.cnn.com",
    "DNT": "1",
    "Connection": "keep-alive",
}

# Ensure the data folder exists
DATA_FOLDER = "data"
os.makedirs(DATA_FOLDER, exist_ok=True)

# Define file path
CSV_FILE = os.path.join(DATA_FOLDER, "fg_index.csv")

def get_fear_greed_index():
    """Fetches Fear & Greed Index from CNN and extracts the score"""
    today = datetime.today().strftime('%Y%m%dT')  # Format: YYYYMMDDT
    url = CNN_FEAR_GREED_URL + datetime.today().strftime('%Y-%m-%d')

    try:
        response = requests.get(url, headers=HEADERS)
        print("Response Status Code:", response.status_code)

        if response.status_code == 200 and response.text.strip():
            data = response.json()
            fear_greed_score = round(data["fear_and_greed"]["score"], 2)

            return today, fear_greed_score
        else:
            print("Error: Empty response or invalid status code.")
            return None, None
    except Exception as e:
        print("Error fetching data:", e)
        return None, None

def update_csv(date, index_value):
    """Writes or appends to fg_index.csv in the correct TradingView format"""
    # Format line in (date, open, high, low, close, volume) format
    line = f"{date},{index_value},{index_value},{index_value},{index_value},0\n"

    # Check if file exists to avoid duplicates
    if os.path.exists(CSV_FILE):
        with open(CSV_FILE, "r") as file:
            lines = file.readlines()
            last_line = lines[-1].strip() if lines else None
            # Prevent duplicate entry
            if last_line and last_line.startswith(date):
                print("⚠️ Data for today already exists. Skipping update.")
                return
    
    # Append data
    with open(CSV_FILE, "a") as file:
        file.write(line)

    print(f"✅ Updated {CSV_FILE} with Fear & Greed Index: {index_value}")

def push_to_github():
    """Pushes updated CSV file to GitHub"""
    try:
        subprocess.run(["git", "add", CSV_FILE], check=True)
        subprocess.run(["git", "commit", "-m", f"Updated fg_index.csv with Fear & Greed Index"], check=True)
        subprocess.run(["git", "push", "origin", "main"], check=True)
        print("✅ Pushed updated data to GitHub.")
    except subprocess.CalledProcessError as e:
        print("❌ Error pushing to GitHub:", e)

if __name__ == "__main__":
    date, index_value = get_fear_greed_index()
    if index_value is not None:
        update_csv(date, index_value)
        push_to_github()

    # Run every hour
    time.sleep(3600)
