import requests
import json
import time
from datetime import datetime
import subprocess

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

def get_fear_greed_index():
    """Fetches Fear & Greed Index from CNN and extracts the score"""
    today = datetime.today().strftime('%Y-%m-%d')
    url = CNN_FEAR_GREED_URL + today

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

def update_github(date, index_value):
    """Updates fear_greed_data.csv and pushes to GitHub"""
    filename = "fear_greed_data.csv"

    # Update the CSV file
    with open(filename, mode="w") as file:
        file.write("date,index\n")
        file.write(f"{date},{index_value}\n")
    
    print(f"✅ Updated {filename} with Fear & Greed Index: {index_value}")

    # Push to GitHub
    subprocess.run(["git", "add", filename])
    subprocess.run(["git", "commit", "-m", f"Updated Fear & Greed Index: {index_value}"])
    subprocess.run(["git", "push", "origin", "main"])
    print("✅ Pushed updated data to GitHub.")

if __name__ == "__main__":
    date, index_value = get_fear_greed_index()
    if index_value is not None:
        update_github(date, index_value)
    
    # Run every hour
    time.sleep(3600)
